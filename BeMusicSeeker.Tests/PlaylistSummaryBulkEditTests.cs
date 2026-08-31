using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
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
using BeMusicSeeker.Views.Settings;
using BeMusicSeeker.Views.Settings.Pages;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.BmsPlaylistTestSupport;

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
    [TestCategory("Playlist")]
    public async Task PlaylistSummaryBulkExternalPropertyInitialization_DurableFailureSuppressesLatePresentation()
    {
        bool previousEnablePlaylistUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        Settings.Default.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistSummaryBulkEditTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerPath = Path.Combine(tempDirectory, "header.json");
            string scorePath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(
                headerPath,
                CreateUtf8BomBytes("{\"name\":\"External Name\",\"symbol\":\"E\",\"data_url\":\"./score.json\"}"));
            File.WriteAllBytes(
                scorePath,
                CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(
                songDbPath,
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(
                    songDbPath,
                    CustomFolderOutputPhysicalSurface.Empty));
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerPath));
            table.playlist_id = 8101;
            table.name = "Local Name";
            table.symbol = "L";
            table.Header_url = new Uri(headerPath);
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);

            PlaylistWorkspaceViewModel workspace = CreatePlaylistWorkspace(
                playlist,
                library: null,
                settingsProvider: () => new CustomFolderOutputSettingsSnapshot
                {
                    OperationModeLR2DB = false
                });
            int summaryRefreshCount = 0;
            int catalogChangedCount = 0;
            int keywordValueCandidatesChangedCount = 0;
            workspace.PlaylistPresentationRefreshRequested += (_, _) => summaryRefreshCount++;
            workspace.PlaylistCatalogChanged += (_, _) => catalogChangedCount++;
            workspace.PlaylistKeywordValueCandidatesChanged += (_, _) => keywordValueCandidatesChangedCount++;

            File.Delete(songDbPath);
            Directory.CreateDirectory(songDbPath);

            Exception? failure = null;
            try
            {
                await workspace.ApplyPlaylistSummaryExternalPropertyInitializationAsync(
                    [new PlaylistSummaryRow { TableRef = table }],
                    new PlaylistWorkspaceViewModel.PlaylistSummaryExternalPropertyInitializationOptions
                    {
                        Name = true,
                        Symbol = true
                    });
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            Assert.IsNotNull(failure);

            Assert.AreEqual(0, summaryRefreshCount);
            Assert.AreEqual(0, catalogChangedCount);
            Assert.AreEqual(0, keywordValueCandidatesChangedCount);
        }
        finally
        {
            Settings.Default.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
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
                windowTest.ShowAndWaitForContentRendered(view);

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
    public void PlaylistPropertyDialog_RendersHistoricalUpdateDateWithoutTime()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var view = new PlaylistPropertyDialog
            {
                DataContext = new PlaylistPropertyDateFixture()
            };

            try
            {
                windowTest.ShowAndWaitForContentRendered(view);
                view.UpdateLayout();

                string[] renderedText = FindDescendants<TextBlock>(view).Select(textBlock => textBlock.Text).ToArray();
                Assert.IsTrue(
                    renderedText.Contains("Update: 2024/01/02", StringComparer.Ordinal),
                    $"Rendered text did not contain the historical date: {string.Join(" | ", renderedText)}");
            }
            finally
            {
                view.CloseForOwnerShutdown();
            }
        });
    }

    [TestMethod]
    public void PlaylistPropertyDialog_TopNavigationKeepsSingleSelectionAndDraftsAcrossCategories()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var fixture = new PlaylistPropertyPresentationFixture
            {
                OperationModeLR2DB = true
            };
            var view = new PlaylistPropertyDialog
            {
                DataContext = fixture
            };

            try
            {
                windowTest.ShowAndWaitForContentRendered(view);
                view.UpdateLayout();

                FrameworkElement navigation = (FrameworkElement)view.FindName("propertyNavigation")
                    ?? throw new AssertFailedException("Playlist property navigation was not materialized.");
                var contentScrollViewer = (ScrollViewer)view.FindName("propertyContentScrollViewer");
                Assert.IsNotNull(contentScrollViewer);
                FrameworkElement contentHost = (FrameworkElement)view.FindName("propertyContent")
                    ?? throw new AssertFailedException("Playlist property content host was not materialized.");
                PropertyNavigationObservation navigationState = ObservePropertyNavigation(navigation, contentHost);
                AssertSelectedPropertyCategory(navigationState, PropertyNavigationCategory.General);
                Assert.AreSame(
                    navigationState.ItemsByCategory[PropertyNavigationCategory.General],
                    navigationState.InitiallySelectedItem,
                    "The property dialog must capture its initial General provider before category navigation.");
                Assert.AreNotSame(navigation, contentHost);

                TextBox nameEditor = FindBoundElement<TextBox>(
                    view,
                    TextBox.TextProperty,
                    nameof(PlaylistPropertyPresentationFixture.name));
                Assert.IsTrue(nameEditor.IsVisible, "The initially selected category must expose the name field.");
                nameEditor.Text = "draft playlist";
                nameEditor.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                TestUiDispatcherHost.Drain();

                navigationState.ItemsByCategory[PropertyNavigationCategory.CustomFolder].Provider.Select();
                TestUiDispatcherHost.Drain();
                AssertSelectedPropertyCategory(navigationState, PropertyNavigationCategory.CustomFolder);
                Assert.IsFalse(nameEditor.IsVisible, "Unselected category fields must not remain visible.");
                TextBox outputDirectoryEditor = FindBoundElement<TextBox>(
                    view,
                    TextBox.TextProperty,
                    nameof(PlaylistPropertyPresentationFixture.output_dir));
                Assert.IsTrue(outputDirectoryEditor.IsVisible, "The selected Custom Folder category must expose its fields.");
                FrameworkElement customPage = (FrameworkElement)view.FindName("customPage")
                    ?? throw new AssertFailedException("Custom Folder page was not materialized.");
                customPage.MinHeight = contentScrollViewer.ViewportHeight + 160d;
                contentScrollViewer.UpdateLayout();
                Assert.IsTrue(
                    contentScrollViewer.ScrollableHeight > 0,
                    "Test-induced overflow must expose a scrollable viewport for reset coverage.");
                contentScrollViewer.ScrollToVerticalOffset(contentScrollViewer.ScrollableHeight);
                TestUiDispatcherHost.Drain();
                Assert.IsTrue(contentScrollViewer.VerticalOffset > 0);

                navigationState.ItemsByCategory[PropertyNavigationCategory.Folder].Provider.Select();
                TestUiDispatcherHost.Drain();
                AssertSelectedPropertyCategory(navigationState, PropertyNavigationCategory.Folder);
                Assert.AreEqual(0, contentScrollViewer.VerticalOffset);
                Assert.IsFalse(outputDirectoryEditor.IsVisible, "The deselected Custom Folder fields must be hidden.");
                navigationState.ItemsByCategory[PropertyNavigationCategory.General].Provider.Select();
                TestUiDispatcherHost.Drain();
                AssertSelectedPropertyCategory(navigationState, PropertyNavigationCategory.General);
                Assert.IsTrue(nameEditor.IsVisible, "Returning to General must restore its observable fields.");
                Assert.AreEqual("draft playlist", nameEditor.Text);
                Assert.AreEqual("draft playlist", fixture.name);
                Assert.AreEqual(0, contentScrollViewer.VerticalOffset);
            }
            finally
            {
                view.CloseForOwnerShutdown();
            }
        });
    }

    [TestMethod]
    public void PlaylistPropertyDialog_PreservesInventoryBindingsAndAvailabilityGates()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var fixture = new PlaylistPropertyPresentationFixture
            {
                OperationModeLR2DB = true
            };
            var view = new PlaylistPropertyDialog
            {
                DataContext = fixture,
                Width = 640,
                Height = 520
            };

            try
            {
                windowTest.ShowAndWaitForContentRendered(view);
                view.UpdateLayout();

                foreach (string path in new[]
                {
                    nameof(PlaylistPropertyPresentationFixture.name),
                    nameof(PlaylistPropertyPresentationFixture.symbol),
                    nameof(PlaylistPropertyPresentationFixture.compat_prefix),
                    nameof(PlaylistPropertyPresentationFixture.Page_url),
                    nameof(PlaylistPropertyPresentationFixture.Header_url),
                    nameof(PlaylistPropertyPresentationFixture.Data_url),
                    nameof(PlaylistPropertyPresentationFixture.output_dir)
                })
                {
                    TextBox editor = FindBoundElement<TextBox>(view, TextBox.TextProperty, path);
                    AssertEffectiveTwoWayBinding(editor, TextBox.TextProperty, path);
                }

                ComboBox entryType = FindBoundElement<ComboBox>(
                    view,
                    Selector.SelectedItemProperty,
                    nameof(PlaylistPropertyPresentationFixture.entry_type));
                AssertEffectiveTwoWayBinding(entryType, Selector.SelectedItemProperty, nameof(PlaylistPropertyPresentationFixture.entry_type));
                ComboBox entryTypeList = FindBoundElement<ComboBox>(
                    view,
                    ItemsControl.ItemsSourceProperty,
                    nameof(PlaylistPropertyPresentationFixture.entry_type_list));
                AssertEffectiveOneWayBinding(
                    entryTypeList,
                    ItemsControl.ItemsSourceProperty,
                    nameof(PlaylistPropertyPresentationFixture.entry_type_list));
                CheckBox externalSync = FindBoundCheckBox(view, nameof(PlaylistPropertyPresentationFixture.is_external_sync));
                AssertEffectiveTwoWayBinding(externalSync, nameof(PlaylistPropertyPresentationFixture.is_external_sync));

                ComboBox sortKey = FindBoundElement<ComboBox>(
                    view,
                    Selector.SelectedItemProperty,
                    nameof(PlaylistPropertyPresentationFixture.folder_sort_key));
                AssertEffectiveTwoWayBinding(sortKey, Selector.SelectedItemProperty, nameof(PlaylistPropertyPresentationFixture.folder_sort_key));
                ComboBox sortKeyList = FindBoundElement<ComboBox>(
                    view,
                    ItemsControl.ItemsSourceProperty,
                    nameof(PlaylistPropertyPresentationFixture.folder_sort_key_list));
                AssertEffectiveOneWayBinding(
                    sortKeyList,
                    ItemsControl.ItemsSourceProperty,
                    nameof(PlaylistPropertyPresentationFixture.folder_sort_key_list));
                ItemsControl folderOrder = FindBoundElement<ItemsControl>(
                    view,
                    ItemsControl.ItemsSourceProperty,
                    nameof(PlaylistPropertyPresentationFixture.folder_order));
                AssertEffectiveOneWayBinding(
                    folderOrder,
                    ItemsControl.ItemsSourceProperty,
                    nameof(PlaylistPropertyPresentationFixture.folder_order));
                RadioButton[] sortOrderButtons = FindDescendants<RadioButton>(view)
                    .Where(button => BindingOperations.GetBindingBase(button, ToggleButton.IsCheckedProperty) is Binding binding
                        && binding.Path?.Path == nameof(PlaylistPropertyPresentationFixture.folder_sort_ascending))
                    .ToArray();
                Assert.AreEqual(2, sortOrderButtons.Length, "Both ascending and descending order choices must remain bound.");
                RadioButton ascendingOrder = sortOrderButtons.Single(button =>
                    ((Binding)BindingOperations.GetBindingBase(button, ToggleButton.IsCheckedProperty)!).Converter is null);
                RadioButton descendingOrder = sortOrderButtons.Single(button =>
                    ((Binding)BindingOperations.GetBindingBase(button, ToggleButton.IsCheckedProperty)!).Converter is not null);
                AssertEffectiveTwoWayBinding(
                    ascendingOrder,
                    ToggleButton.IsCheckedProperty,
                    "ascending folder order");
                AssertEffectiveTwoWayBinding(
                    descendingOrder,
                    ToggleButton.IsCheckedProperty,
                    "descending folder order");
                CheckBox autoSort = FindBoundCheckBox(view, nameof(PlaylistPropertyPresentationFixture.is_auto_folder_sort));
                AssertEffectiveTwoWayBinding(autoSort, nameof(PlaylistPropertyPresentationFixture.is_auto_folder_sort));

                ComboBox outputBase = FindBoundElement<ComboBox>(
                    view,
                    Selector.SelectedItemProperty,
                    nameof(PlaylistPropertyPresentationFixture.custom_folder_output_base_option));
                AssertEffectiveTwoWayBinding(outputBase, Selector.SelectedItemProperty, nameof(PlaylistPropertyPresentationFixture.custom_folder_output_base_option));
                ComboBox outputBaseList = FindBoundElement<ComboBox>(
                    view,
                    ItemsControl.ItemsSourceProperty,
                    nameof(PlaylistPropertyPresentationFixture.OutputBaseOptions));
                AssertEffectiveOneWayBinding(
                    outputBaseList,
                    ItemsControl.ItemsSourceProperty,
                    nameof(PlaylistPropertyPresentationFixture.OutputBaseOptions));
                AssertEffectiveTwoWayBinding(
                    FindBoundCheckBox(view, nameof(PlaylistPropertyPresentationFixture.is_root_folder)),
                    nameof(PlaylistPropertyPresentationFixture.is_root_folder));

                TextBlock updateDate = FindBoundElement<TextBlock>(
                    view,
                    TextBlock.TextProperty,
                    nameof(PlaylistPropertyPresentationFixture.last_update));
                AssertEffectiveOneWayBinding(
                    updateDate,
                    TextBlock.TextProperty,
                    nameof(PlaylistPropertyPresentationFixture.last_update));
                foreach (TextBlock infoText in FindDescendants<TextBlock>(view)
                    .Where(textBlock => BindingOperations.GetBindingBase(textBlock, TextBlock.TextProperty) is Binding))
                {
                    AssertEffectiveOneWayBinding(
                        infoText,
                        TextBlock.TextProperty,
                        $"informational text '{((Binding)BindingOperations.GetBindingBase(infoText, TextBlock.TextProperty)!).Path?.Path}'");
                }
                foreach (ContentControl infoContent in FindDescendants<ContentControl>(view)
                    .Where(contentControl => BindingOperations.GetBindingBase(contentControl, ContentControl.ContentProperty) is Binding))
                {
                    AssertEffectiveOneWayBinding(
                        infoContent,
                        ContentControl.ContentProperty,
                        $"informational content '{((Binding)BindingOperations.GetBindingBase(infoContent, ContentControl.ContentProperty)!).Path?.Path}'");
                }

                string[] customFolderTypes =
                [
                    nameof(LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder),
                    nameof(LR2SongDBExtended.playlist.CustomFolderType.UserFolder),
                    nameof(LR2SongDBExtended.playlist.CustomFolderType.LevelFolder),
                    nameof(LR2SongDBExtended.playlist.CustomFolderType.AlphabetFolder),
                    nameof(LR2SongDBExtended.playlist.CustomFolderType.ClearFolder),
                    nameof(LR2SongDBExtended.playlist.CustomFolderType.DJLevelFolder),
                    nameof(LR2SongDBExtended.playlist.CustomFolderType.CategoryAllFolder),
                    nameof(LR2SongDBExtended.playlist.CustomFolderType.OtherFolder),
                    nameof(LR2SongDBExtended.playlist.CustomFolderType.RandomFolder),
                    nameof(LR2SongDBExtended.playlist.CustomFolderType.BpmSortFolder),
                    nameof(LR2SongDBExtended.playlist.CustomFolderType.BpSortFolder),
                    nameof(LR2SongDBExtended.playlist.CustomFolderType.PlayCountSortFolder),
                    nameof(LR2SongDBExtended.playlist.CustomFolderType.LastPlaySortFolder)
                ];
                CheckBox[] outputCheckBoxes = FindDescendants<CheckBox>(view)
                    .Where(checkBox => BindingOperations.GetBindingBase(
                            checkBox,
                            ToggleButton.IsCheckedProperty) is Binding binding
                        && binding.ConverterParameter is string)
                    .ToArray();
                CollectionAssert.AreEquivalent(
                    customFolderTypes,
                    outputCheckBoxes.Select(checkBox =>
                    {
                        Binding binding = (Binding)BindingOperations.GetBindingBase(
                            checkBox,
                            ToggleButton.IsCheckedProperty)!;
                        return (string)binding.ConverterParameter;
                    }).ToArray());
                foreach (CheckBox checkBox in outputCheckBoxes)
                {
                    Binding binding = (Binding)BindingOperations.GetBindingBase(checkBox, ToggleButton.IsCheckedProperty)!;
                    AssertEffectiveTwoWayBinding(checkBox, binding.ConverterParameter as string ?? "custom folder output");
                    Binding? tooltipBinding = BindingOperations.GetBindingBase(
                        checkBox,
                        ToolTipService.ToolTipProperty) as Binding;
                    Assert.IsNotNull(tooltipBinding);
                    AssertEffectiveOneWayBinding(
                        checkBox,
                        ToolTipService.ToolTipProperty,
                        $"tooltip for {binding.ConverterParameter}");
                    Assert.IsTrue(
                        (tooltipBinding.Path?.Path ?? string.Empty).EndsWith("_tooltip", StringComparison.Ordinal),
                        $"Missing semantic tooltip binding for {binding.ConverterParameter}.");
                }

                FrameworkElement navigation = (FrameworkElement)view.FindName("propertyNavigation")
                    ?? throw new AssertFailedException("Playlist property navigation was not materialized.");
                FrameworkElement contentHost = (FrameworkElement)view.FindName("propertyContent")
                    ?? throw new AssertFailedException("Playlist property content host was not materialized.");
                PropertyNavigationObservation navigationState = ObservePropertyNavigation(navigation, contentHost);
                PropertyNavigationItem customNavigationItem = navigationState.ItemsByCategory[PropertyNavigationCategory.CustomFolder];
                Assert.IsTrue(customNavigationItem.Peer.IsEnabled(), "Custom Folder navigation must initially be available.");
                fixture.OperationModeLR2DB = false;
                TestUiDispatcherHost.Drain();
                Assert.IsFalse(customNavigationItem.Peer.IsEnabled(), "Custom Folder navigation must follow OperationModeLR2DB.");
                fixture.OperationModeLR2DB = true;
                TestUiDispatcherHost.Drain();
                Assert.IsTrue(customNavigationItem.Peer.IsEnabled(), "Custom Folder navigation must recover when the mode returns.");

                TextBox pageUrl = FindBoundElement<TextBox>(
                    view,
                    TextBox.TextProperty,
                    nameof(PlaylistPropertyPresentationFixture.Page_url));
                Assert.IsTrue(pageUrl.IsEnabled);
                externalSync.IsChecked = true;
                externalSync.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
                TestUiDispatcherHost.Drain();
                Assert.IsFalse(pageUrl.IsEnabled);
                externalSync.IsChecked = false;
                externalSync.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
                TestUiDispatcherHost.Drain();
                Assert.IsTrue(pageUrl.IsEnabled);

                Button moveUp = FindButtonByAutomationId(view, "PlaylistPropertyFolderMoveUp");
                Button moveDown = FindButtonByAutomationId(view, "PlaylistPropertyFolderMoveDown");
                Assert.IsTrue(moveUp.IsEnabled);
                Assert.IsTrue(moveDown.IsEnabled);
                autoSort.IsChecked = true;
                autoSort.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
                TestUiDispatcherHost.Drain();
                Assert.IsFalse(moveUp.IsEnabled);
                Assert.IsFalse(moveDown.IsEnabled);
                autoSort.IsChecked = false;
                autoSort.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
                TestUiDispatcherHost.Drain();

                entryType.SelectedItem = Resources.Folder;
                entryType.GetBindingExpression(Selector.SelectedItemProperty)!.UpdateSource();
                TestUiDispatcherHost.Drain();
                CheckBox levelFolder = outputCheckBoxes.Single(checkBox =>
                    string.Equals(
                        (string?)((Binding)BindingOperations.GetBindingBase(checkBox, ToggleButton.IsCheckedProperty)!).ConverterParameter,
                        nameof(LR2SongDBExtended.playlist.CustomFolderType.LevelFolder),
                        StringComparison.Ordinal));
                Assert.IsFalse(levelFolder.IsEnabled);
                entryType.SelectedItem = Resources.File;
                entryType.GetBindingExpression(Selector.SelectedItemProperty)!.UpdateSource();
                TestUiDispatcherHost.Drain();
                Assert.IsTrue(levelFolder.IsEnabled);
            }
            finally
            {
                view.CloseForOwnerShutdown();
            }
        });
    }

    [TestMethod]
    public void PlaylistPropertyDialog_ConceptAShellAndGeneralUseTheApprovedResponsiveViewport()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var fixture = new PlaylistPropertyPresentationFixture
            {
                OperationModeLR2DB = true
            };
            var view = new PlaylistPropertyDialog
            {
                DataContext = fixture
            };

            try
            {
                Assert.AreEqual(640d, view.Width, 0.01d);
                Assert.AreEqual(720d, view.Height, 0.01d);
                Assert.AreEqual(560d, view.MinWidth, 0.01d);
                Assert.AreEqual(420d, view.MinHeight, 0.01d);
                Assert.AreEqual(ResizeMode.CanResize, view.ResizeMode);

                windowTest.ShowAndWaitForContentRendered(view);
                view.UpdateLayout();

                var bodyViewport = (ScrollViewer)view.FindName("propertyContentScrollViewer")
                    ?? throw new AssertFailedException("Playlist property body viewport was not materialized.");
                FrameworkElement navigation = (FrameworkElement)view.FindName("propertyNavigation")
                    ?? throw new AssertFailedException("Playlist property navigation was not materialized.");
                var navigationList = (ListBox)navigation;
                var contentHost = (ContentControl)view.FindName("propertyContent")
                    ?? throw new AssertFailedException("Playlist property content host was not materialized.");
                TextBlock updateDate = FindBoundElement<TextBlock>(
                    view,
                    TextBlock.TextProperty,
                    nameof(PlaylistPropertyPresentationFixture.last_update));
                Button accept = FindButtonByAutomationId(view, "PlaylistPropertyAccept");

                Assert.AreEqual(0d, bodyViewport.ScrollableHeight, 0.01d,
                    "Initial Japanese General content must fit without vertical page overflow.");
                Assert.AreEqual(0d, bodyViewport.ScrollableWidth, 0.01d,
                    "The page body must not expose horizontal overflow.");
                Assert.AreEqual(ScrollBarVisibility.Disabled, bodyViewport.HorizontalScrollBarVisibility);
                Assert.IsFalse(IsDescendantOf(navigation, bodyViewport), "Navigation must remain fixed outside the page body viewport.");
                Assert.IsFalse(IsDescendantOf(updateDate, bodyViewport), "Update information must remain fixed outside the page body viewport.");
                Assert.IsFalse(IsDescendantOf(accept, bodyViewport), "Dialog actions must remain fixed outside the page body viewport.");
                for (int categoryIndex = 0; categoryIndex < navigationList.Items.Count; categoryIndex++)
                {
                    navigationList.SelectedIndex = categoryIndex;
                    TestUiDispatcherHost.Drain();
                    view.UpdateLayout();
                    AssertNoVisiblePageEnclosingChrome(contentHost, bodyViewport, categoryIndex);
                    AssertSelectedPageRetainsMajorSectionHierarchy(view, categoryIndex);
                }
                navigationList.SelectedIndex = 0;
                TestUiDispatcherHost.Drain();
                view.UpdateLayout();

                TextBox name = FindBoundElement<TextBox>(view, TextBox.TextProperty, nameof(PlaylistPropertyPresentationFixture.name));
                TextBox symbol = FindBoundElement<TextBox>(view, TextBox.TextProperty, nameof(PlaylistPropertyPresentationFixture.symbol));
                TextBox prefix = FindBoundElement<TextBox>(view, TextBox.TextProperty, nameof(PlaylistPropertyPresentationFixture.compat_prefix));
                ComboBox entryType = FindBoundElement<ComboBox>(
                    view,
                    Selector.SelectedItemProperty,
                    nameof(PlaylistPropertyPresentationFixture.entry_type));
                CheckBox externalSync = FindBoundCheckBox(view, nameof(PlaylistPropertyPresentationFixture.is_external_sync));
                TextBox pageUrl = FindBoundElement<TextBox>(view, TextBox.TextProperty, nameof(PlaylistPropertyPresentationFixture.Page_url));
                TextBox headerUrl = FindBoundElement<TextBox>(view, TextBox.TextProperty, nameof(PlaylistPropertyPresentationFixture.Header_url));
                TextBox dataUrl = FindBoundElement<TextBox>(view, TextBox.TextProperty, nameof(PlaylistPropertyPresentationFixture.Data_url));

                FrameworkElement[] canonicalEditors = [name, symbol, prefix, entryType, pageUrl, headerUrl, dataUrl];
                Assert.IsTrue(canonicalEditors.All(editor => editor.ActualHeight >= 32d),
                    "General editors must retain canonical minimum height.");

                Rect nameBounds = GetBounds(name, bodyViewport);
                Rect symbolBounds = GetBounds(symbol, bodyViewport);
                Rect prefixBounds = GetBounds(prefix, bodyViewport);
                Rect entryBounds = GetBounds(entryType, bodyViewport);
                Rect syncBounds = GetBounds(externalSync, bodyViewport);
                Rect pageBounds = GetBounds(pageUrl, bodyViewport);
                Rect headerBounds = GetBounds(headerUrl, bodyViewport);
                Rect dataBounds = GetBounds(dataUrl, bodyViewport);

                Assert.IsTrue(nameBounds.Left <= symbolBounds.Left && nameBounds.Right >= prefixBounds.Right,
                    "The full-width name editor must span the two-column identity editors.");
                Assert.IsTrue(symbolBounds.Bottom > prefixBounds.Top && prefixBounds.Bottom > symbolBounds.Top,
                    "Symbol and compatible prefix must share the roomy identity row.");
                Assert.IsTrue(symbolBounds.Width >= symbolBounds.Height * 3d,
                    "Symbol must not retain the former compact fixed-width editor.");
                Assert.IsTrue(entryBounds.Top >= Math.Max(symbolBounds.Bottom, prefixBounds.Bottom),
                    "Entry unit must follow the identity editors as a separate field.");
                Assert.IsTrue(syncBounds.Top >= entryBounds.Bottom,
                    "External synchronization must follow the local playlist identity fields.");
                Assert.IsTrue(pageBounds.Top >= syncBounds.Bottom
                    && headerBounds.Top >= pageBounds.Bottom
                    && dataBounds.Top >= headerBounds.Bottom,
                    "External synchronization must precede the full-width Page/Header/Data URI sequence.");
                Assert.IsTrue(new[] { pageBounds, headerBounds, dataBounds }.All(bounds => bounds.Width >= nameBounds.Width * 0.9d),
                    "URI editors must use the same full-width editing span as the playlist name.");

                double symbolWidthBeforeResize = symbol.ActualWidth;
                double nameWidthBeforeResize = name.ActualWidth;
                double pageWidthBeforeResize = pageUrl.ActualWidth;
                view.Width += 160d;
                view.UpdateLayout();
                TestUiDispatcherHost.Drain();
                Assert.IsTrue(symbol.ActualWidth > symbolWidthBeforeResize);
                Assert.IsTrue(name.ActualWidth > nameWidthBeforeResize);
                Assert.IsTrue(pageUrl.ActualWidth > pageWidthBeforeResize);
                Assert.AreEqual(0d, bodyViewport.ScrollableWidth, 0.01d);

                view.Width = view.MinWidth;
                view.Height = view.MinHeight;
                view.UpdateLayout();
                TestUiDispatcherHost.Drain();
                Assert.AreEqual(0d, bodyViewport.ScrollableWidth, 0.01d,
                    "General must not create horizontal overflow at minimum window size.");
                Assert.IsTrue(
                    canonicalEditors
                        .Select(editor => GetBounds(editor, bodyViewport))
                        .All(bounds => bounds.Left >= -0.5d && bounds.Right <= bodyViewport.ViewportWidth + 0.5d),
                    "General editors must remain horizontally reachable at minimum window size.");
                bodyViewport.ScrollToVerticalOffset(bodyViewport.ScrollableHeight);
                TestUiDispatcherHost.Drain();
                Rect minimumDataBounds = GetBounds(dataUrl, bodyViewport);
                Assert.IsTrue(
                    minimumDataBounds.Top >= -0.5d && minimumDataBounds.Bottom <= bodyViewport.ViewportHeight + 0.5d,
                    "The final General editor must be reachable through the minimum-height body viewport.");
            }
            finally
            {
                view.CloseForOwnerShutdown();
            }
        });
    }

    [TestMethod]
    public void PlaylistPropertyDialog_ConceptAFolderOrderOwnsScrollingAndGrowsWithWindow()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var fixture = new PlaylistPropertyPresentationFixture
            {
                OperationModeLR2DB = true
            };
            for (int index = 0; index < 40; index++)
            {
                fixture.folder_order.Add($"Synthetic folder {index:D2}");
            }

            var view = new PlaylistPropertyDialog
            {
                DataContext = fixture
            };

            try
            {
                windowTest.ShowAndWaitForContentRendered(view);
                ((ListBox)view.FindName("propertyNavigation")).SelectedIndex = 1;
                TestUiDispatcherHost.Drain();
                view.UpdateLayout();

                var bodyViewport = (ScrollViewer)view.FindName("propertyContentScrollViewer")!;
                ComboBox sortKey = FindBoundElement<ComboBox>(
                    view,
                    Selector.SelectedItemProperty,
                    nameof(PlaylistPropertyPresentationFixture.folder_sort_key));
                var folderOrder = FindBoundElement<ListBox>(
                    view,
                    ItemsControl.ItemsSourceProperty,
                    nameof(PlaylistPropertyPresentationFixture.folder_order));
                Button moveUp = FindButtonByAutomationId(view, "PlaylistPropertyFolderMoveUp");
                Button moveDown = FindButtonByAutomationId(view, "PlaylistPropertyFolderMoveDown");
                ScrollViewer listViewport = FindDescendants<ScrollViewer>(folderOrder)
                    .Single(viewer => !ReferenceEquals(viewer, bodyViewport));

                Rect sortBounds = GetBounds(sortKey, bodyViewport);
                Rect listBounds = GetBounds(folderOrder, bodyViewport);
                Rect moveUpBounds = GetBounds(moveUp, bodyViewport);
                Rect moveDownBounds = GetBounds(moveDown, bodyViewport);
                Assert.IsTrue(sortBounds.Bottom <= listBounds.Top,
                    "Folder sorting must precede the folder-order region.");
                Assert.IsTrue(listViewport.ScrollableHeight > 0d,
                    "Synthetic folders must overflow inside the independently scrolling order list.");
                Assert.AreEqual(0d, bodyViewport.ScrollableWidth, 0.01d);
                Assert.IsTrue(moveUpBounds.Top >= listBounds.Top && moveDownBounds.Bottom <= listBounds.Bottom,
                    "Manual ordering actions must remain reachable alongside the folder-order viewport.");

                double listHeightBeforeResize = folderOrder.ActualHeight;
                view.Height += 160d;
                view.UpdateLayout();
                TestUiDispatcherHost.Drain();
                Assert.IsTrue(folderOrder.ActualHeight > listHeightBeforeResize,
                    "The dominant folder-order viewport must gain height when the window becomes taller.");

                SettingsSection folderOrderSection = FindDescendants<SettingsSection>(view)
                    .Single(section => IsDescendantOf(folderOrder, section));
                BindingOperations.ClearBinding(folderOrderSection, SettingsSection.DescriptionProperty);
                folderOrderSection.Description = string.Join(
                    " ",
                    Enumerable.Repeat("Deterministic long folder order description", 16));
                view.Width = view.MinWidth;
                view.Height = view.MinHeight;
                view.UpdateLayout();
                TestUiDispatcherHost.Drain();

                Assert.AreEqual(0d, bodyViewport.ScrollableWidth, 0.01d,
                    "Folder must not create horizontal overflow at minimum window size.");
                Assert.IsTrue(bodyViewport.ScrollableHeight > 0d,
                    "Constrained long Folder content must create reachable outer vertical extent.");
                Assert.IsTrue(listViewport.ScrollableHeight > 0d,
                    "The Folder list must retain independent vertical extent at minimum window size.");
                bodyViewport.ScrollToVerticalOffset(bodyViewport.ScrollableHeight);
                TestUiDispatcherHost.Drain();
                Rect minimumMoveUpBounds = GetBounds(moveUp, bodyViewport);
                Rect minimumMoveDownBounds = GetBounds(moveDown, bodyViewport);
                Assert.IsTrue(
                    minimumMoveUpBounds.Top >= -0.5d
                    && minimumMoveDownBounds.Bottom <= bodyViewport.ViewportHeight + 0.5d,
                    "The Folder move actions must be reachable through the minimum-height body viewport.");
                Assert.IsTrue(
                    new[] { GetBounds(sortKey, bodyViewport), GetBounds(folderOrder, bodyViewport), minimumMoveUpBounds, minimumMoveDownBounds }
                        .All(bounds => bounds.Left >= -0.5d && bounds.Right <= bodyViewport.ViewportWidth + 0.5d),
                    "Folder controls must remain horizontally reachable at minimum window size.");
            }
            finally
            {
                view.CloseForOwnerShutdown();
            }
        });
    }

    [TestMethod]
    public void PlaylistPropertyDialog_ConceptACustomOutputFolderPrecedesNaturalFlowDestination()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var fixture = new PlaylistPropertyPresentationFixture
            {
                OperationModeLR2DB = true
            };
            var view = new PlaylistPropertyDialog
            {
                DataContext = fixture
            };

            try
            {
                windowTest.ShowAndWaitForContentRendered(view);
                view.Width = view.MinWidth;
                ((ListBox)view.FindName("propertyNavigation")).SelectedIndex = 2;
                TestUiDispatcherHost.Drain();
                view.UpdateLayout();

                var bodyViewport = (ScrollViewer)view.FindName("propertyContentScrollViewer")!;
                ComboBox outputBase = FindBoundElement<ComboBox>(
                    view,
                    Selector.SelectedItemProperty,
                    nameof(PlaylistPropertyPresentationFixture.custom_folder_output_base_option));
                TextBox outputDirectory = FindBoundElement<TextBox>(
                    view,
                    TextBox.TextProperty,
                    nameof(PlaylistPropertyPresentationFixture.output_dir));
                CheckBox rootFolder = FindBoundCheckBox(view, nameof(PlaylistPropertyPresentationFixture.is_root_folder));
                CheckBox[] flags = FindDescendants<CheckBox>(view)
                    .Where(checkBox => BindingOperations.GetBindingBase(
                            checkBox,
                            ToggleButton.IsCheckedProperty) is Binding binding
                        && binding.ConverterParameter is string)
                    .ToArray();
                FrameworkElement outputTypesSection = FindElementByAutomationId<FrameworkElement>(
                    view,
                    "PlaylistPropertyOutputTypesSection");
                TextBlock explanation = FindDescendants<TextBlock>(outputTypesSection)
                    .Where(textBlock => !string.IsNullOrWhiteSpace(textBlock.Text))
                    .OrderBy(textBlock => GetBounds(textBlock, bodyViewport).Top)
                    .Skip(1)
                    .FirstOrDefault()
                    ?? throw new AssertFailedException("Output Types description was not rendered in its heading hierarchy.");

                Rect outputBaseBounds = GetBounds(outputBase, bodyViewport);
                Rect outputDirectoryBounds = GetBounds(outputDirectory, bodyViewport);
                Rect rootBounds = GetBounds(rootFolder, bodyViewport);
                Rect explanationBounds = GetBounds(explanation, bodyViewport);
                Rect firstFlagBounds = flags.Select(flag => GetBounds(flag, bodyViewport)).OrderBy(bounds => bounds.Top).First();
                Assert.IsTrue(explanationBounds.Bottom <= firstFlagBounds.Top,
                    "Output Types explanation must follow its heading and precede the flags.");
                double lastFlagBottom = flags.Max(flag => GetBounds(flag, bodyViewport).Bottom);
                Assert.IsTrue(lastFlagBottom <= outputBaseBounds.Top
                    && outputBaseBounds.Bottom <= outputDirectoryBounds.Top
                    && outputDirectoryBounds.Bottom <= rootBounds.Top,
                    "Output Folder must precede the naturally flowing Output Destination fields.");
                Assert.AreEqual(13, flags.Length);
                Assert.AreEqual(0d, bodyViewport.ScrollableWidth, 0.01d);

                double outputBaseWidthBeforeResize = outputBase.ActualWidth;
                double outputDirectoryWidthBeforeResize = outputDirectory.ActualWidth;
                view.Width += 160d;
                view.UpdateLayout();
                TestUiDispatcherHost.Drain();
                Assert.IsTrue(outputBase.ActualWidth > outputBaseWidthBeforeResize);
                Assert.IsTrue(outputDirectory.ActualWidth > outputDirectoryWidthBeforeResize);

                double destinationTopBeforeHeightResize = GetBounds(outputBase, bodyViewport).Top;
                view.Height += 160d;
                view.UpdateLayout();
                TestUiDispatcherHost.Drain();
                Assert.AreEqual(
                    destinationTopBeforeHeightResize,
                    GetBounds(outputBase, bodyViewport).Top,
                    1d,
                    "Output Destination must remain in natural top-origin flow when only height grows.");

                CheckBox wrappingProbe = flags.OrderBy(flag => GetBounds(flag, bodyViewport).Top).First();
                double ordinaryFlagHeight = flags
                    .Where(flag => !ReferenceEquals(flag, wrappingProbe))
                    .Max(flag => flag.ActualHeight);
                BindingOperations.ClearBinding(wrappingProbe, ContentControl.ContentProperty);
                wrappingProbe.Content = string.Join(" ", Enumerable.Repeat("Long localized output option", 12));
                view.Width = view.MinWidth;
                view.UpdateLayout();
                TestUiDispatcherHost.Drain();
                Assert.IsTrue(wrappingProbe.ActualHeight > ordinaryFlagHeight,
                    "A long localized output caption must wrap instead of remaining a clipped single line.");
                Assert.AreEqual(0d, bodyViewport.ScrollableWidth, 0.01d);
                Assert.IsTrue(flags
                    .Select(flag => GetBounds(flag, bodyViewport))
                    .All(bounds => bounds.Left >= -0.5d && bounds.Right <= bodyViewport.ViewportWidth + 0.5d),
                    "Output flags must remain horizontally contained at minimum width.");
            }
            finally
            {
                view.CloseForOwnerShutdown();
            }
        });
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
            windowTest.ShowAndWaitForContentRendered(view);

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

    private sealed class PlaylistPropertyDateFixture
    {
        public DateTime last_update { get; } = new(2024, 1, 2, 3, 4, 5);
    }

    private sealed class PlaylistPropertyPresentationFixture : INotifyPropertyChanged
    {
        private bool operationModeLr2Db;
        private bool isExternalSync;
        private bool isAutoFolderSort;
        private bool isRootFolder;
        private bool folderSortAscending = true;
        private LR2SongDBExtended.playlist.EntryUnitType entryType = LR2SongDBExtended.playlist.EntryUnitType.File;
        private LR2SongDBExtended.playlist.CustomFolderSortType folderSortKey;
        private LR2SongDBExtended.playlist.CustomFolderType ignoreFolderOutput;
        private string nameValue = "Presentation playlist";
        private string symbolValue = "P";
        private string compatPrefix = "★";
        private string outputDirectory = "presentation-playlist";
        private Uri pageUrl = new("https://example.invalid/page", UriKind.Absolute);
        private Uri headerUrl = new("https://example.invalid/header", UriKind.Absolute);
        private Uri dataUrl = new("https://example.invalid/data", UriKind.Absolute);
        private PlaylistCustomFolderOutputBaseOption outputBaseOption = new("Default", null);

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool OperationModeLR2DB
        {
            get => operationModeLr2Db;
            set => SetField(ref operationModeLr2Db, value);
        }

        public bool is_external_sync
        {
            get => isExternalSync;
            set => SetField(ref isExternalSync, value);
        }

        public bool is_auto_folder_sort
        {
            get => isAutoFolderSort;
            set => SetField(ref isAutoFolderSort, value);
        }

        public bool is_root_folder
        {
            get => isRootFolder;
            set => SetField(ref isRootFolder, value);
        }

        public bool folder_sort_ascending
        {
            get => folderSortAscending;
            set => SetField(ref folderSortAscending, value);
        }

        public LR2SongDBExtended.playlist.EntryUnitType entry_type
        {
            get => entryType;
            set => SetField(ref entryType, value);
        }

        public LR2SongDBExtended.playlist.CustomFolderSortType folder_sort_key
        {
            get => folderSortKey;
            set => SetField(ref folderSortKey, value);
        }

        public LR2SongDBExtended.playlist.CustomFolderType ignore_folder_output
        {
            get => ignoreFolderOutput;
            set => SetField(ref ignoreFolderOutput, value);
        }

        public string name
        {
            get => nameValue;
            set => SetField(ref nameValue, value);
        }

        public string symbol
        {
            get => symbolValue;
            set => SetField(ref symbolValue, value);
        }

        public string compat_prefix
        {
            get => compatPrefix;
            set => SetField(ref compatPrefix, value);
        }

        public string output_dir
        {
            get => outputDirectory;
            set => SetField(ref outputDirectory, value);
        }

        public Uri Page_url
        {
            get => pageUrl;
            set => SetField(ref pageUrl, value);
        }

        public Uri Header_url
        {
            get => headerUrl;
            set => SetField(ref headerUrl, value);
        }

        public Uri Data_url
        {
            get => dataUrl;
            set => SetField(ref dataUrl, value);
        }

        public DateTime last_update { get; } = new(2024, 1, 2, 3, 4, 5);

        public IEnumerable<string> entry_type_list => ["ファイル", "フォルダ"];

        public IEnumerable<string> folder_sort_key_list => ["(無し)", "レベル", "タイトル"];

        public ObservableCollection<string> folder_order { get; } = ["Alpha", "Beta", "Gamma"];

        public IReadOnlyList<PlaylistCustomFolderOutputBaseOption> OutputBaseOptions { get; } =
            [new PlaylistCustomFolderOutputBaseOption("Default", null)];

        public PlaylistCustomFolderOutputBaseOption custom_folder_output_base_option
        {
            get => outputBaseOption;
            set => SetField(ref outputBaseOption, value);
        }

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
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
        AssertEffectiveTwoWayBinding(checkBox, ToggleButton.IsCheckedProperty, propertyName);
    }

    private static void AssertEffectiveTwoWayBinding(
        DependencyObject element,
        DependencyProperty property,
        string propertyName)
    {
        BindingExpression binding = BindingOperations.GetBindingExpression(element, property)
            ?? throw new AssertFailedException($"Missing binding: {propertyName}");
        Assert.IsTrue(BindingOperations.IsDataBound(element, property), propertyName);
        BindingMode mode = binding.ParentBinding.Mode;
        bool isEffectiveTwoWay = mode == BindingMode.TwoWay
            || (mode == BindingMode.Default
                && ((FrameworkPropertyMetadata)property
                    .GetMetadata(element.GetType())).BindsTwoWayByDefault);
        Assert.IsTrue(
            isEffectiveTwoWay,
            $"{propertyName} must use an effective TwoWay binding; actual mode was {mode}.");
    }

    private static void AssertEffectiveOneWayBinding(
        DependencyObject element,
        DependencyProperty property,
        string propertyName)
    {
        BindingExpression binding = BindingOperations.GetBindingExpression(element, property)
            ?? throw new AssertFailedException($"Missing binding: {propertyName}");
        Assert.IsTrue(BindingOperations.IsDataBound(element, property), propertyName);
        BindingMode mode = binding.ParentBinding.Mode;
        bool isEffectiveOneWay = mode == BindingMode.OneWay
            || (mode == BindingMode.Default
                && !((FrameworkPropertyMetadata)property
                    .GetMetadata(element.GetType())).BindsTwoWayByDefault);
        Assert.IsTrue(
            isEffectiveOneWay,
            $"{propertyName} must use an effective OneWay binding; actual mode was {mode}.");
    }

    private static PropertyNavigationObservation ObservePropertyNavigation(
        FrameworkElement navigation,
        FrameworkElement contentHost)
    {
        AutomationPeer navigationPeer = UIElementAutomationPeer.CreatePeerForElement(navigation)
            ?? throw new AssertFailedException("Playlist property navigation automation was not materialized.");
        ISelectionProvider selectionProvider = navigationPeer.GetPattern(PatternInterface.Selection) as ISelectionProvider
            ?? throw new AssertFailedException("Playlist property navigation does not expose Selection automation.");
        Assert.IsFalse(selectionProvider.CanSelectMultiple);
        AutomationPeer[] navigationItems = navigationPeer.GetChildren()?.ToArray()
            ?? throw new AssertFailedException("Playlist property navigation items were not exposed to Automation.");
        Assert.AreEqual(3, navigationItems.Length, "Playlist property navigation must expose three category peers.");
        PropertyNavigationItem[] selectionItems = navigationItems
            .Select(CreatePropertyNavigationItem)
            .ToArray();
        Assert.IsTrue(selectionItems.All(item => !string.IsNullOrWhiteSpace(item.Peer.GetName())));
        Assert.AreEqual(1, selectionItems.Count(item => item.Provider.IsSelected));
        Assert.AreEqual(1, selectionProvider.GetSelection().Length);

        PropertyNavigationItem initiallySelectedItem = selectionItems.Single(item => item.Provider.IsSelected);
        PropertyNavigationCategory initialCategory = IdentifyVisiblePropertyCategory(contentHost);

        var itemsByCategory = new Dictionary<PropertyNavigationCategory, PropertyNavigationItem>();
        Assert.IsTrue(
            itemsByCategory.TryAdd(initialCategory, initiallySelectedItem),
            "The initially selected property provider must map to one observable content role.");
        foreach (PropertyNavigationItem item in selectionItems)
        {
            if (ReferenceEquals(item, initiallySelectedItem))
            {
                continue;
            }

            PropertyNavigationCategory category;
            if (!item.Peer.IsEnabled())
            {
                AssertAuthorityDefinedCustomFolderAvailability(navigation, item.Peer);
                PropertyNavigationItem selectedBeforeAttempt = selectionItems.Single(candidate => candidate.Provider.IsSelected);
                bool rejected = false;
                try
                {
                    item.Provider.Select();
                }
                catch (ElementNotEnabledException)
                {
                    rejected = true;
                }
                TestUiDispatcherHost.Drain();
                Assert.IsTrue(
                    rejected || !item.Provider.IsSelected,
                    "A disabled property navigation provider must reject Automation selection.");
                Assert.IsTrue(
                    selectedBeforeAttempt.Provider.IsSelected,
                    "A rejected disabled property navigation selection must leave the current category selected.");
                category = PropertyNavigationCategory.CustomFolder;
            }
            else
            {
                item.Provider.Select();
                TestUiDispatcherHost.Drain();
                category = IdentifyVisiblePropertyCategory(contentHost);
            }

            Assert.IsTrue(
                itemsByCategory.TryAdd(category, item),
                $"Multiple navigation items expose the {category} property content.");
        }

        Assert.AreEqual(3, itemsByCategory.Count, "Property navigation categories must map to unique observable content roles.");
        Assert.AreEqual(
            PropertyNavigationCategory.General,
            initialCategory,
            "The SelectionItem provider captured before any mutation must map to the General content role.");
        Assert.AreSame(
            initiallySelectedItem,
            itemsByCategory[PropertyNavigationCategory.General],
            "The captured initial SelectionItem must be the provider mapped to General.");
        initiallySelectedItem.Provider.Select();
        TestUiDispatcherHost.Drain();
        return new PropertyNavigationObservation(selectionProvider, itemsByCategory, initiallySelectedItem);
    }

    private static PropertyNavigationItem CreatePropertyNavigationItem(AutomationPeer peer)
        => new(
            peer,
            peer.GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider
                ?? throw new AssertFailedException("A property navigation item lacks SelectionItem automation."));

    private static void AssertSelectedPropertyCategory(
        PropertyNavigationObservation navigation,
        PropertyNavigationCategory expectedCategory,
        string message = null)
    {
        string assertionMessage = message ?? $"Property navigation category {expectedCategory} must be selected.";
        Assert.AreEqual(1, navigation.SelectionProvider.GetSelection().Length, assertionMessage);
        Assert.AreEqual(1, navigation.ItemsByCategory.Values.Count(item => item.Provider.IsSelected), assertionMessage);
        Assert.IsTrue(navigation.ItemsByCategory[expectedCategory].Provider.IsSelected, assertionMessage);
    }

    private static PropertyNavigationCategory IdentifyVisiblePropertyCategory(FrameworkElement contentHost)
    {
        var visibleCategories = new List<PropertyNavigationCategory>();
        if (HasVisibleBinding(contentHost, nameof(PlaylistPropertyDialogViewModel.name)))
        {
            visibleCategories.Add(PropertyNavigationCategory.General);
        }

        if (HasVisibleBinding(contentHost, nameof(PlaylistPropertyDialogViewModel.folder_sort_key))
            || HasVisibleBinding(contentHost, nameof(PlaylistPropertyDialogViewModel.folder_order)))
        {
            visibleCategories.Add(PropertyNavigationCategory.Folder);
        }

        if (HasVisibleBinding(contentHost, nameof(PlaylistPropertyDialogViewModel.output_dir)))
        {
            visibleCategories.Add(PropertyNavigationCategory.CustomFolder);
        }

        Assert.AreEqual(
            1,
            visibleCategories.Count,
            "Exactly one authority-backed Property category content role must be visible.");
        return visibleCategories[0];
    }

    private static bool HasVisibleBinding(DependencyObject root, string path)
    {
        return FindDescendants<FrameworkElement>(root)
            .Any(element => element.IsVisible
                && element.ActualWidth > 0d
                && element.ActualHeight > 0d
                && HasBindingPath(element, path));
    }

    private static bool HasBindingPath(FrameworkElement element, string path)
    {
        LocalValueEnumerator localValues = element.GetLocalValueEnumerator();
        while (localValues.MoveNext())
        {
            if (localValues.Current.Value is BindingExpressionBase expression
                && expression.ParentBindingBase is Binding binding
                && string.Equals(binding.Path?.Path, path, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void AssertAuthorityDefinedCustomFolderAvailability(
        FrameworkElement navigation,
        AutomationPeer expectedProvider)
    {
        FrameworkElement[] availabilityHosts = FindDescendants<FrameworkElement>(navigation)
            .Where(element => HasBindingPath(
                element,
                nameof(PlaylistPropertyDialogViewModel.OperationModeLR2DB)))
            .ToArray();
        Assert.AreEqual(
            1,
            availabilityHosts.Length,
            "The conditionally available property category must expose one authority-backed availability binding.");
        Assert.IsFalse(
            availabilityHosts[0].IsEnabled,
            "The authority-backed custom-folder availability binding must be disabled for a rejected provider.");
        AutomationPeer availabilityPeer = UIElementAutomationPeer.CreatePeerForElement(availabilityHosts[0])
            ?? throw new AssertFailedException(
                "The authority-backed custom-folder availability host must expose the disabled SelectionItem provider.");
        Assert.AreEqual(
            expectedProvider.GetAutomationControlType(),
            availabilityPeer.GetAutomationControlType(),
            "The disabled provider must retain the authority-backed custom-folder Automation role.");
        Assert.AreEqual(
            expectedProvider.GetBoundingRectangle(),
            availabilityPeer.GetBoundingRectangle(),
            "The disabled provider must be the authority-backed custom-folder navigation role.");
    }

    private sealed record PropertyNavigationObservation(
        ISelectionProvider SelectionProvider,
        IReadOnlyDictionary<PropertyNavigationCategory, PropertyNavigationItem> ItemsByCategory,
        PropertyNavigationItem InitiallySelectedItem);

    private sealed record PropertyNavigationItem(
        AutomationPeer Peer,
        ISelectionItemProvider Provider);

    private enum PropertyNavigationCategory
    {
        General,
        Folder,
        CustomFolder,
    }

    private static T FindBoundElement<T>(
        DependencyObject root,
        DependencyProperty property,
        string path)
        where T : DependencyObject
    {
        return FindDescendants<T>(root)
            .SingleOrDefault(element =>
                BindingOperations.GetBindingBase(element, property) is Binding binding
                && string.Equals(binding.Path?.Path, path, StringComparison.Ordinal))
            ?? throw new AssertFailedException($"Missing binding '{path}' on {typeof(T).Name}.");
    }

    private static CheckBox FindBoundCheckBox(DependencyObject root, string path)
        => FindBoundElement<CheckBox>(root, ToggleButton.IsCheckedProperty, path);

    private static Button FindButtonByAutomationId(DependencyObject root, string automationId)
    {
        return FindDescendants<Button>(root)
            .SingleOrDefault(button => string.Equals(
                AutomationProperties.GetAutomationId(button),
                automationId,
                StringComparison.Ordinal))
            ?? throw new AssertFailedException($"Missing button '{automationId}'.");
    }

    private static T FindElementByAutomationId<T>(DependencyObject root, string automationId)
        where T : FrameworkElement
    {
        return FindDescendants<T>(root)
            .SingleOrDefault(element => string.Equals(
                AutomationProperties.GetAutomationId(element),
                automationId,
                StringComparison.Ordinal))
            ?? throw new AssertFailedException($"Missing element '{automationId}'.");
    }

    private static Rect GetBounds(FrameworkElement element, Visual ancestor)
    {
        return element.TransformToAncestor(ancestor)
            .TransformBounds(new Rect(0d, 0d, element.ActualWidth, element.ActualHeight));
    }

    private static bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
    {
        return FindDescendants<DependencyObject>(ancestor)
            .Any(candidate => ReferenceEquals(candidate, element));
    }

    private static void AssertNoVisiblePageEnclosingChrome(
        ContentControl contentHost,
        ScrollViewer bodyViewport,
        int categoryIndex)
    {
        Rect viewportBounds = GetBounds(bodyViewport, contentHost);
        Border? enclosingChrome = FindDescendants<Border>(contentHost)
            .FirstOrDefault(border =>
            {
                if (!IsDescendantOf(bodyViewport, border))
                {
                    return false;
                }

                Rect borderBounds = GetBounds(border, contentHost);
                bool coversBody = borderBounds.Left <= viewportBounds.Left + 0.5d
                    && borderBounds.Top <= viewportBounds.Top + 0.5d
                    && borderBounds.Right >= viewportBounds.Right - 0.5d
                    && borderBounds.Bottom >= viewportBounds.Bottom - 0.5d;
                bool hasVisibleBorder = (border.BorderThickness.Left > 0d
                        || border.BorderThickness.Top > 0d
                        || border.BorderThickness.Right > 0d
                        || border.BorderThickness.Bottom > 0d)
                    && IsVisibleBrush(border.BorderBrush);
                bool hasRoundedVisibleSurface = (border.CornerRadius.TopLeft > 0d
                        || border.CornerRadius.TopRight > 0d
                        || border.CornerRadius.BottomRight > 0d
                        || border.CornerRadius.BottomLeft > 0d)
                    && (IsVisibleBrush(border.Background) || hasVisibleBorder);
                return coversBody && (hasVisibleBorder || hasRoundedVisibleSurface);
            });

        Assert.IsNull(
            enclosingChrome,
            $"Playlist property category {categoryIndex} must use an unframed selected-page body.");
    }

    private static void AssertSelectedPageRetainsMajorSectionHierarchy(
        PlaylistPropertyDialog view,
        int categoryIndex)
    {
        FrameworkElement firstControl;
        FrameworkElement secondControl;
        string firstRole;
        string secondRole;
        switch ((PropertyNavigationCategory)categoryIndex)
        {
            case PropertyNavigationCategory.General:
                firstControl = FindBoundElement<TextBox>(
                    view,
                    TextBox.TextProperty,
                    nameof(PlaylistPropertyPresentationFixture.name));
                secondControl = FindBoundCheckBox(
                    view,
                    nameof(PlaylistPropertyPresentationFixture.is_external_sync));
                firstRole = "General identity";
                secondRole = "General external synchronization";
                break;
            case PropertyNavigationCategory.Folder:
                firstControl = FindBoundElement<ComboBox>(
                    view,
                    Selector.SelectedItemProperty,
                    nameof(PlaylistPropertyPresentationFixture.folder_sort_key));
                secondControl = FindBoundElement<ListBox>(
                    view,
                    ItemsControl.ItemsSourceProperty,
                    nameof(PlaylistPropertyPresentationFixture.folder_order));
                firstRole = "Folder sorting";
                secondRole = "Folder ordering";
                break;
            case PropertyNavigationCategory.CustomFolder:
                firstControl = FindDescendants<CheckBox>(view)
                    .First(checkBox => BindingOperations.GetBindingBase(
                            checkBox,
                            ToggleButton.IsCheckedProperty) is Binding binding
                        && binding.ConverterParameter is string);
                secondControl = FindBoundElement<ComboBox>(
                    view,
                    Selector.SelectedItemProperty,
                    nameof(PlaylistPropertyPresentationFixture.custom_folder_output_base_option));
                firstRole = "Custom Output Folder";
                secondRole = "Custom Output Destination";
                break;
            default:
                throw new AssertFailedException($"Unknown playlist property category index {categoryIndex}.");
        }

        SettingsSection? firstSection = FindDescendants<SettingsSection>(view)
            .SingleOrDefault(section => section.IsVisible && IsDescendantOf(firstControl, section));
        SettingsSection? secondSection = FindDescendants<SettingsSection>(view)
            .SingleOrDefault(section => section.IsVisible && IsDescendantOf(secondControl, section));
        Assert.IsNotNull(firstSection, $"{firstRole} must remain owned by a visible major SettingsSection.");
        Assert.IsNotNull(secondSection, $"{secondRole} must remain owned by a visible major SettingsSection.");
        Assert.AreNotSame(
            firstSection,
            secondSection,
            $"{firstRole} and {secondRole} must remain partitioned by the major SettingsSection hierarchy.");
    }

    private static bool IsVisibleBrush(Brush? brush)
        => brush is not null
            && brush.Opacity > 0d
            && (brush is not SolidColorBrush solid || solid.Color.A > 0);

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
