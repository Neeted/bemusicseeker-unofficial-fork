using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Settings.Pages;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistSummaryBulkEditTests
{
    [TestMethod]
    public void ResolvePlaylistSummaryCustomFolderOutputState_ReturnsValueWhenAllTablesMatch()
    {
        BMSTable first = CreateTable(LR2SongDBExtended.playlist.CustomFolderType.ClearFolder);
        BMSTable second = CreateTable(LR2SongDBExtended.playlist.CustomFolderType.AlphabetFolder);

        bool? state = PlaylistWorkspaceViewModel.ResolvePlaylistSummaryCustomFolderOutputState(
            [first, second],
            LR2SongDBExtended.playlist.CustomFolderType.UserFolder);

        Assert.AreEqual(true, state);
    }

    [TestMethod]
    public void ResolvePlaylistSummaryCustomFolderOutputState_ReturnsNullWhenTablesAreMixed()
    {
        BMSTable enabled = CreateTable(LR2SongDBExtended.playlist.CustomFolderType.None);
        BMSTable disabled = CreateTable(LR2SongDBExtended.playlist.CustomFolderType.UserFolder);

        bool? state = PlaylistWorkspaceViewModel.ResolvePlaylistSummaryCustomFolderOutputState(
            [enabled, disabled],
            LR2SongDBExtended.playlist.CustomFolderType.UserFolder);

        Assert.IsNull(state);
    }

    [TestMethod]
    public void ApplyPlaylistSummaryCustomFolderOutputPatchToMask_KeepsIndeterminateValues()
    {
        BMSTable table = CreateTable(LR2SongDBExtended.playlist.CustomFolderType.UserFolder | LR2SongDBExtended.playlist.CustomFolderType.ClearFolder);
        var patch = new PlaylistWorkspaceViewModel.PlaylistSummaryCustomFolderOutputPatch
        {
            UserFolder = null,
            ClearFolder = true
        };

        LR2SongDBExtended.playlist.CustomFolderType next = PlaylistWorkspaceViewModel.ApplyPlaylistSummaryCustomFolderOutputPatchToMask(table, table.ignore_folder_output, patch);

        Assert.IsFalse(LR2SongDBExtended.playlist.IsCustomFolderTypeEnabled(next, LR2SongDBExtended.playlist.CustomFolderType.UserFolder));
        Assert.IsTrue(LR2SongDBExtended.playlist.IsCustomFolderTypeEnabled(next, LR2SongDBExtended.playlist.CustomFolderType.ClearFolder));
    }

    [TestMethod]
    public void ApplyPlaylistSummaryCustomFolderOutputPatchToMask_DisablesLevelFolderForFolderEntryUnit()
    {
        BMSTable table = CreateTable(
            LR2SongDBExtended.playlist.CustomFolderType.None,
            LR2SongDBExtended.playlist.EntryUnitType.Folder);
        var patch = new PlaylistWorkspaceViewModel.PlaylistSummaryCustomFolderOutputPatch
        {
            LevelFolder = true
        };

        LR2SongDBExtended.playlist.CustomFolderType next = PlaylistWorkspaceViewModel.ApplyPlaylistSummaryCustomFolderOutputPatchToMask(table, table.ignore_folder_output, patch);

        Assert.IsFalse(LR2SongDBExtended.playlist.IsCustomFolderTypeEnabled(next, LR2SongDBExtended.playlist.CustomFolderType.LevelFolder));
    }

    [TestMethod]
    public void ResolvePlaylistSummaryCustomFolderOutputState_TreatsOldAllFoldersMaskAsNewTypesEnabled()
    {
        BMSTable table = CreateTable((LR2SongDBExtended.playlist.CustomFolderType)0x7F);

        Assert.AreEqual(
            true,
            PlaylistWorkspaceViewModel.ResolvePlaylistSummaryCustomFolderOutputState(
                [table],
                LR2SongDBExtended.playlist.CustomFolderType.LastPlaySortFolder));
        Assert.AreEqual(
            true,
            PlaylistWorkspaceViewModel.ResolvePlaylistSummaryCustomFolderOutputState(
                [table],
                LR2SongDBExtended.playlist.CustomFolderType.BpmSortFolder));
        Assert.AreEqual(
            true,
            PlaylistWorkspaceViewModel.ResolvePlaylistSummaryCustomFolderOutputState(
                [table],
                LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder));
    }

    [TestMethod]
    public void PlaylistSummaryBulkEditDialogViewModel_AllowsLastPlaySortFolderRegardlessOfSchemaStatus()
    {
        var owner = MainWindowViewModelTestFactory.Create();
        var dialog = new PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel(
            owner.PlaylistWorkspace,
            []);

        dialog.OutputLastPlaySortFolder = true;

        Assert.IsTrue(dialog.CanApplyCustomFolderOutput);
    }

    [TestMethod]
    public void PlaylistPropertyDialog_LastPlaySortFolderRemainsEnabledWithoutSchemaStatus()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistSummaryBulkEditTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        PlaylistPropertyDialogViewModel? dialog = null;
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            File.WriteAllBytes(songDbPath, []);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            BMSTable table = new()
            {
                name = "Last Play Sort",
                Output_dir = "last-play-sort",
                entry_type = LR2SongDBExtended.playlist.EntryUnitType.File,
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.None
            };
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            var service = new PlaylistPropertySaveService(
                () => playlist,
                () => null!,
                () => null!,
                () => new CustomFolderOutputSettingsSnapshot
                {
                    OperationModeLR2DB = false
                });
            PlaylistPropertyEditSession session = service.CreateEditSessionAsync(table)
                .GetAwaiter()
                .GetResult()
                ?? throw new AssertFailedException("A playlist property edit session was not created.");
            dialog = new PlaylistPropertyDialogViewModel(service, session);

            TestUiDispatcherHost.RunWindowTest(windowTest =>
            {
                var view = new PlaylistPropertyDialog
                {
                    DataContext = dialog
                };
                var window = new Window
                {
                    Content = view,
                    Width = 640,
                    Height = 520
                };
                windowTest.ShowAndWaitForContentRendered(window);

                Dictionary<string, CheckBox> checkBoxes = FindDescendants<CheckBox>(view)
                    .Select(checkBox =>
                    {
                        string? parameter = checkBox.GetBindingExpression(ToggleButton.IsCheckedProperty)
                            ?.ParentBinding.ConverterParameter as string;
                        return (Parameter: parameter, CheckBox: checkBox);
                    })
                    .Where(item => item.Parameter != null)
                    .ToDictionary(item => item.Parameter!, item => item.CheckBox, StringComparer.Ordinal);
                IReadOnlyList<(LR2SongDBExtended.playlist.CustomFolderType Type, string BulkProperty, string DefaultProperty, string Label)> options =
                    GetCustomFolderOptionContracts();
                CollectionAssert.AreEquivalent(
                    options.Select(option => option.Type.ToString()).ToArray(),
                    checkBoxes.Keys.ToArray());

                foreach ((LR2SongDBExtended.playlist.CustomFolderType type, _, _, string label) in options)
                {
                    CheckBox checkBox = checkBoxes[type.ToString()];
                    Assert.AreEqual(label, checkBox.Content, type.ToString());
                    AssertEffectiveTwoWayBinding(checkBox, type.ToString());

                    dialog.ignore_folder_output = type;
                    TestUiDispatcherHost.Drain();
                    Assert.AreEqual(false, checkBox.IsChecked, type.ToString());
                    dialog.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.None;
                    TestUiDispatcherHost.Drain();
                    Assert.AreEqual(true, checkBox.IsChecked, type.ToString());

                    checkBox.IsChecked = false;
                    checkBox.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
                    TestUiDispatcherHost.Drain();
                    Assert.IsFalse(
                        LR2SongDBExtended.playlist.IsCustomFolderTypeEnabled(dialog.ignore_folder_output, type),
                        type.ToString());
                    checkBox.IsChecked = true;
                    checkBox.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
                    TestUiDispatcherHost.Drain();
                    Assert.IsTrue(
                        LR2SongDBExtended.playlist.IsCustomFolderTypeEnabled(dialog.ignore_folder_output, type),
                        type.ToString());
                }

                CheckBox lastPlaySort = checkBoxes[nameof(LR2SongDBExtended.playlist.CustomFolderType.LastPlaySortFolder)];
                Assert.IsTrue(lastPlaySort.IsEnabled,
                    "Last Play Sort must remain selectable when the optional schema is unavailable.");

            });
        }
        finally
        {
            dialog?.Dispose();
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void PlaylistSummaryBulkEditDialog_BindsAllCustomFolderOptionsTwoWayAndLocalized()
    {
        var settings = new Settings
        {
            OperationModeLR2DB = true
        };
        MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(settings);
        BMSTable table = CreateTable(LR2SongDBExtended.playlist.CustomFolderType.None);
        var dialog = new PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel(
            owner.PlaylistWorkspace,
            [new PlaylistSummaryRow { TableRef = table }]);

        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var view = new PlaylistSummaryBulkEditDialog
            {
                DataContext = dialog
            };
            var window = new Window
            {
                Content = view,
                Width = 640,
                Height = 520
            };
            windowTest.ShowAndWaitForContentRendered(window);

            Dictionary<string, CheckBox> checkBoxes = FindDescendants<CheckBox>(view)
                .Select(checkBox =>
                {
                    string? path = checkBox.GetBindingExpression(ToggleButton.IsCheckedProperty)
                        ?.ParentBinding.Path?.Path;
                    return (Path: path, CheckBox: checkBox);
                })
                .Where(item => item.Path?.StartsWith("Output", StringComparison.Ordinal) == true)
                .ToDictionary(item => item.Path!, item => item.CheckBox, StringComparer.Ordinal);

            IReadOnlyList<(LR2SongDBExtended.playlist.CustomFolderType Type, string BulkProperty, string DefaultProperty, string Label)> options =
                GetCustomFolderOptionContracts();
            CollectionAssert.AreEquivalent(
                options.Select(option => option.BulkProperty).ToArray(),
                checkBoxes.Keys.ToArray());

            foreach ((_, string propertyName, _, string label) in options)
            {
                CheckBox checkBox = checkBoxes[propertyName];
                Assert.AreEqual(label, checkBox.Content, propertyName);
                AssertEffectiveTwoWayBinding(checkBox, propertyName);

                SetBulkFolderValue(dialog, propertyName, false);
                TestUiDispatcherHost.Drain();
                Assert.AreEqual(false, checkBox.IsChecked, propertyName);
                SetBulkFolderValue(dialog, propertyName, true);
                TestUiDispatcherHost.Drain();
                Assert.AreEqual(true, checkBox.IsChecked, propertyName);

                checkBox.IsChecked = false;
                checkBox.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
                TestUiDispatcherHost.Drain();
                Assert.AreEqual(false, GetBulkFolderValue(dialog, propertyName), propertyName);
                checkBox.IsChecked = true;
                checkBox.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
                TestUiDispatcherHost.Drain();
                Assert.AreEqual(true, GetBulkFolderValue(dialog, propertyName), propertyName);
            }
        });
    }

    [TestMethod]
    public void SettingDialog_CustomFolderOutputDefaultsRenderLocalizedTwoWayFolderOptions()
    {
        var settings = new Settings
        {
            OperationModeLR2DB = true
        };
        MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(settings);

        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var window = new SettingsWindow
            {
                DataContext = owner.SettingDialog,
                Width = 820,
                Height = 600
            };
            windowTest.ShowAndWaitForContentRendered(window);
            ((ListBox)window.FindName("settingsNavigation")).SelectedIndex = 5;
            TestUiDispatcherHost.Drain();
            var page = (PlaylistSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;

            Dictionary<string, CheckBox> checkBoxes = FindDescendants<CheckBox>(page)
                .Select(checkBox =>
                {
                    string? path = checkBox.GetBindingExpression(ToggleButton.IsCheckedProperty)
                        ?.ParentBinding.Path?.Path;
                    return (Path: path, CheckBox: checkBox);
                })
                .Where(item => item.Path?.StartsWith("DefaultOutput", StringComparison.Ordinal) == true)
                .ToDictionary(item => item.Path!, item => item.CheckBox, StringComparer.Ordinal);

            IReadOnlyList<(LR2SongDBExtended.playlist.CustomFolderType Type, string BulkProperty, string DefaultProperty, string Label)> options =
                GetCustomFolderOptionContracts();
            CollectionAssert.AreEquivalent(
                options.Select(option => option.DefaultProperty).ToArray(),
                checkBoxes.Keys.ToArray());

            foreach ((_, _, string propertyName, string label) in options)
            {
                CheckBox checkBox = checkBoxes[propertyName];
                Assert.AreEqual(label, checkBox.Content, propertyName);
                AssertEffectiveTwoWayBinding(checkBox, propertyName);

                SetDefaultFolderValue(owner.SettingDialog, propertyName, false);
                TestUiDispatcherHost.Drain();
                Assert.AreEqual(false, checkBox.IsChecked, propertyName);
                SetDefaultFolderValue(owner.SettingDialog, propertyName, true);
                TestUiDispatcherHost.Drain();
                Assert.AreEqual(true, checkBox.IsChecked, propertyName);

                checkBox.IsChecked = false;
                checkBox.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
                TestUiDispatcherHost.Drain();
                Assert.AreEqual(false, GetDefaultFolderValue(owner.SettingDialog, propertyName), propertyName);
                checkBox.IsChecked = true;
                checkBox.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
                TestUiDispatcherHost.Drain();
                Assert.AreEqual(true, GetDefaultFolderValue(owner.SettingDialog, propertyName), propertyName);
            }
        });
    }

    [TestMethod]
    public void ApplyPlaylistSummaryExternalSyncFlagForTable_KeepsInvalidUrlTableOffWhenEnabling()
    {
        BMSTable table = CreateTable(LR2SongDBExtended.playlist.CustomFolderType.None);

        bool changed = PlaylistWorkspaceViewModel.ApplyPlaylistSummaryExternalSyncFlagForTable(table, isExternalSync: true);

        Assert.IsFalse(changed);
        Assert.IsFalse(table.is_external_sync);
    }

    [TestMethod]
    public void ApplyPlaylistSummaryExternalSyncFlagForTable_EnablesValidUrlTable()
    {
        BMSTable table = CreateTable(LR2SongDBExtended.playlist.CustomFolderType.None);
        table.Header_url = new Uri("https://example.invalid/header.json", UriKind.Absolute);
        table.Data_url = new Uri("data.json", UriKind.Relative);

        bool changed = PlaylistWorkspaceViewModel.ApplyPlaylistSummaryExternalSyncFlagForTable(table, isExternalSync: true);

        Assert.IsTrue(changed);
        Assert.IsTrue(table.is_external_sync);
    }

    private static BMSTable CreateTable(
        LR2SongDBExtended.playlist.CustomFolderType ignoreFolderOutput,
        LR2SongDBExtended.playlist.EntryUnitType entryUnitType = LR2SongDBExtended.playlist.EntryUnitType.File)
    {
        return new BMSTable
        {
            ignore_folder_output = ignoreFolderOutput,
            entry_type = entryUnitType
        };
    }

    private static IReadOnlyList<(LR2SongDBExtended.playlist.CustomFolderType Type, string BulkProperty, string DefaultProperty, string Label)> GetCustomFolderOptionContracts()
    {
        return
        [
            (
                LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                nameof(PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel.OutputAllSongsFolder),
                nameof(SettingsDialogViewModel.DefaultOutputAllSongsFolder),
                Resources.PlaylistProp_ftype_all_songs),
            (
                LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                nameof(PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel.OutputUserFolder),
                nameof(SettingsDialogViewModel.DefaultOutputUserFolder),
                Resources.PlaylistProp_ftype_user),
            (
                LR2SongDBExtended.playlist.CustomFolderType.LevelFolder,
                nameof(PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel.OutputLevelFolder),
                nameof(SettingsDialogViewModel.DefaultOutputLevelFolder),
                Resources.PlaylistProp_ftype_level),
            (
                LR2SongDBExtended.playlist.CustomFolderType.AlphabetFolder,
                nameof(PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel.OutputAlphabetFolder),
                nameof(SettingsDialogViewModel.DefaultOutputAlphabetFolder),
                Resources.PlaylistProp_ftype_alphabet),
            (
                LR2SongDBExtended.playlist.CustomFolderType.ClearFolder,
                nameof(PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel.OutputClearFolder),
                nameof(SettingsDialogViewModel.DefaultOutputClearFolder),
                Resources.PlaylistProp_ftype_clear),
            (
                LR2SongDBExtended.playlist.CustomFolderType.DJLevelFolder,
                nameof(PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel.OutputDJLevelFolder),
                nameof(SettingsDialogViewModel.DefaultOutputDJLevelFolder),
                Resources.PlaylistProp_ftype_djlevel),
            (
                LR2SongDBExtended.playlist.CustomFolderType.CategoryAllFolder,
                nameof(PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel.OutputCategoryAllFolder),
                nameof(SettingsDialogViewModel.DefaultOutputCategoryAllFolder),
                Resources.PlaylistProp_ftype_all),
            (
                LR2SongDBExtended.playlist.CustomFolderType.OtherFolder,
                nameof(PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel.OutputOtherFolder),
                nameof(SettingsDialogViewModel.DefaultOutputOtherFolder),
                Resources.PlaylistProp_ftype_etc),
            (
                LR2SongDBExtended.playlist.CustomFolderType.RandomFolder,
                nameof(PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel.OutputRandomFolder),
                nameof(SettingsDialogViewModel.DefaultOutputRandomFolder),
                Resources.PlaylistProp_ftype_random),
            (
                LR2SongDBExtended.playlist.CustomFolderType.BpmSortFolder,
                nameof(PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel.OutputBpmSortFolder),
                nameof(SettingsDialogViewModel.DefaultOutputBpmSortFolder),
                Resources.PlaylistProp_ftype_bpm_sort),
            (
                LR2SongDBExtended.playlist.CustomFolderType.BpSortFolder,
                nameof(PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel.OutputBpSortFolder),
                nameof(SettingsDialogViewModel.DefaultOutputBpSortFolder),
                Resources.PlaylistProp_ftype_bp_sort),
            (
                LR2SongDBExtended.playlist.CustomFolderType.PlayCountSortFolder,
                nameof(PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel.OutputPlayCountSortFolder),
                nameof(SettingsDialogViewModel.DefaultOutputPlayCountSortFolder),
                Resources.PlaylistProp_ftype_play_count_sort),
            (
                LR2SongDBExtended.playlist.CustomFolderType.LastPlaySortFolder,
                nameof(PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel.OutputLastPlaySortFolder),
                nameof(SettingsDialogViewModel.DefaultOutputLastPlaySortFolder),
                Resources.PlaylistProp_ftype_last_play_sort)
        ];
    }

    private static void AssertEffectiveTwoWayBinding(CheckBox checkBox, string propertyName)
    {
        BindingExpression binding = checkBox.GetBindingExpression(ToggleButton.IsCheckedProperty)
            ?? throw new AssertFailedException($"Missing IsChecked binding: {propertyName}");
        Assert.IsTrue(BindingOperations.IsDataBound(checkBox, ToggleButton.IsCheckedProperty), propertyName);
        BindingMode mode = binding.ParentBinding.Mode;
        bool isEffectiveTwoWay = mode == BindingMode.TwoWay
            || (mode == BindingMode.Default
                && ((FrameworkPropertyMetadata)ToggleButton.IsCheckedProperty
                    .GetMetadata(typeof(ToggleButton))).BindsTwoWayByDefault);
        Assert.IsTrue(
            isEffectiveTwoWay,
            $"{propertyName} must use an effective TwoWay binding; actual mode was {mode}.");
    }

    private static void SetBulkFolderValue(
        PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel dialog,
        string propertyName,
        bool value)
    {
        switch (propertyName)
        {
            case nameof(dialog.OutputAllSongsFolder): dialog.OutputAllSongsFolder = value; break;
            case nameof(dialog.OutputUserFolder): dialog.OutputUserFolder = value; break;
            case nameof(dialog.OutputLevelFolder): dialog.OutputLevelFolder = value; break;
            case nameof(dialog.OutputAlphabetFolder): dialog.OutputAlphabetFolder = value; break;
            case nameof(dialog.OutputClearFolder): dialog.OutputClearFolder = value; break;
            case nameof(dialog.OutputDJLevelFolder): dialog.OutputDJLevelFolder = value; break;
            case nameof(dialog.OutputCategoryAllFolder): dialog.OutputCategoryAllFolder = value; break;
            case nameof(dialog.OutputOtherFolder): dialog.OutputOtherFolder = value; break;
            case nameof(dialog.OutputRandomFolder): dialog.OutputRandomFolder = value; break;
            case nameof(dialog.OutputBpmSortFolder): dialog.OutputBpmSortFolder = value; break;
            case nameof(dialog.OutputBpSortFolder): dialog.OutputBpSortFolder = value; break;
            case nameof(dialog.OutputPlayCountSortFolder): dialog.OutputPlayCountSortFolder = value; break;
            case nameof(dialog.OutputLastPlaySortFolder): dialog.OutputLastPlaySortFolder = value; break;
            default: throw new AssertFailedException($"Unknown bulk folder binding: {propertyName}");
        }
    }

    private static void SetDefaultFolderValue(SettingsDialogViewModel dialog, string propertyName, bool value)
    {
        switch (propertyName)
        {
            case nameof(dialog.DefaultOutputAllSongsFolder): dialog.DefaultOutputAllSongsFolder = value; break;
            case nameof(dialog.DefaultOutputUserFolder): dialog.DefaultOutputUserFolder = value; break;
            case nameof(dialog.DefaultOutputLevelFolder): dialog.DefaultOutputLevelFolder = value; break;
            case nameof(dialog.DefaultOutputAlphabetFolder): dialog.DefaultOutputAlphabetFolder = value; break;
            case nameof(dialog.DefaultOutputClearFolder): dialog.DefaultOutputClearFolder = value; break;
            case nameof(dialog.DefaultOutputDJLevelFolder): dialog.DefaultOutputDJLevelFolder = value; break;
            case nameof(dialog.DefaultOutputCategoryAllFolder): dialog.DefaultOutputCategoryAllFolder = value; break;
            case nameof(dialog.DefaultOutputOtherFolder): dialog.DefaultOutputOtherFolder = value; break;
            case nameof(dialog.DefaultOutputRandomFolder): dialog.DefaultOutputRandomFolder = value; break;
            case nameof(dialog.DefaultOutputBpmSortFolder): dialog.DefaultOutputBpmSortFolder = value; break;
            case nameof(dialog.DefaultOutputBpSortFolder): dialog.DefaultOutputBpSortFolder = value; break;
            case nameof(dialog.DefaultOutputPlayCountSortFolder): dialog.DefaultOutputPlayCountSortFolder = value; break;
            case nameof(dialog.DefaultOutputLastPlaySortFolder): dialog.DefaultOutputLastPlaySortFolder = value; break;
            default: throw new AssertFailedException($"Unknown settings folder binding: {propertyName}");
        }
    }

    private static bool GetBulkFolderValue(
        PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel dialog,
        string propertyName)
    {
        return propertyName switch
        {
            nameof(dialog.OutputAllSongsFolder) => dialog.OutputAllSongsFolder == true,
            nameof(dialog.OutputUserFolder) => dialog.OutputUserFolder == true,
            nameof(dialog.OutputLevelFolder) => dialog.OutputLevelFolder == true,
            nameof(dialog.OutputAlphabetFolder) => dialog.OutputAlphabetFolder == true,
            nameof(dialog.OutputClearFolder) => dialog.OutputClearFolder == true,
            nameof(dialog.OutputDJLevelFolder) => dialog.OutputDJLevelFolder == true,
            nameof(dialog.OutputCategoryAllFolder) => dialog.OutputCategoryAllFolder == true,
            nameof(dialog.OutputOtherFolder) => dialog.OutputOtherFolder == true,
            nameof(dialog.OutputRandomFolder) => dialog.OutputRandomFolder == true,
            nameof(dialog.OutputBpmSortFolder) => dialog.OutputBpmSortFolder == true,
            nameof(dialog.OutputBpSortFolder) => dialog.OutputBpSortFolder == true,
            nameof(dialog.OutputPlayCountSortFolder) => dialog.OutputPlayCountSortFolder == true,
            nameof(dialog.OutputLastPlaySortFolder) => dialog.OutputLastPlaySortFolder == true,
            _ => throw new AssertFailedException($"Unknown bulk folder binding: {propertyName}")
        };
    }

    private static bool GetDefaultFolderValue(SettingsDialogViewModel dialog, string propertyName)
    {
        return propertyName switch
        {
            nameof(dialog.DefaultOutputAllSongsFolder) => dialog.DefaultOutputAllSongsFolder,
            nameof(dialog.DefaultOutputUserFolder) => dialog.DefaultOutputUserFolder,
            nameof(dialog.DefaultOutputLevelFolder) => dialog.DefaultOutputLevelFolder,
            nameof(dialog.DefaultOutputAlphabetFolder) => dialog.DefaultOutputAlphabetFolder,
            nameof(dialog.DefaultOutputClearFolder) => dialog.DefaultOutputClearFolder,
            nameof(dialog.DefaultOutputDJLevelFolder) => dialog.DefaultOutputDJLevelFolder,
            nameof(dialog.DefaultOutputCategoryAllFolder) => dialog.DefaultOutputCategoryAllFolder,
            nameof(dialog.DefaultOutputOtherFolder) => dialog.DefaultOutputOtherFolder,
            nameof(dialog.DefaultOutputRandomFolder) => dialog.DefaultOutputRandomFolder,
            nameof(dialog.DefaultOutputBpmSortFolder) => dialog.DefaultOutputBpmSortFolder,
            nameof(dialog.DefaultOutputBpSortFolder) => dialog.DefaultOutputBpSortFolder,
            nameof(dialog.DefaultOutputPlayCountSortFolder) => dialog.DefaultOutputPlayCountSortFolder,
            nameof(dialog.DefaultOutputLastPlaySortFolder) => dialog.DefaultOutputLastPlaySortFolder,
            _ => throw new AssertFailedException($"Unknown settings folder binding: {propertyName}")
        };
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root == null)
        {
            yield break;
        }

        var pending = new Stack<DependencyObject>();
        var visited = new HashSet<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            DependencyObject current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            if (current is T match)
            {
                yield return match;
            }

            if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
            {
                for (int index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                {
                    pending.Push(VisualTreeHelper.GetChild(current, index));
                }
            }

            foreach (object logicalChild in LogicalTreeHelper.GetChildren(current))
            {
                if (logicalChild is DependencyObject dependencyObject)
                {
                    pending.Push(dependencyObject);
                }
            }
        }
    }
}
