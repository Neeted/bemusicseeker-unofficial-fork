using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MainWindowPlaylistWorkspaceWpfTests
{
    [TestMethod]
    public void PlaylistDialogs_UseDirectWorkspaceComposition()
    {
        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            new Settings(),
            (viewModel, window) =>
            {
                SettingsWindow settingsWindow = null;
                try
                {
                    settingsWindow = window.CreateSettingsWindowForPresentation();

                    Assert.IsFalse(settingsWindow.IsVisible);
                    Assert.AreSame(viewModel.SettingDialog, settingsWindow.DataContext);
                    Assert.AreSame(viewModel.PlaybackPanel, settingsWindow.PlaybackPanel);
                    Assert.AreSame(viewModel.PlaylistWorkspace, settingsWindow.PlaylistWorkspace);

                    FrameworkElement loadPlaylistDialog = (FrameworkElement)window.FindName("loadPlaylistURIDialog");
                    Assert.IsNotNull(loadPlaylistDialog);
                    Assert.AreSame(viewModel.PlaylistWorkspace, loadPlaylistDialog.DataContext);
                }
                finally
                {
                    settingsWindow?.CloseFromPresentation();
                }
            });
    }

    [TestMethod]
    public void PlaylistRootReload_UsesOneTypedTerminalAndHandlesBeforeAsyncCompletion()
    {
        TaskCompletionSource<object?> completion = NewCompletion();
        int callCount = 0;
        MainWindowPlaylistTablesReloadTerminal reloadTerminal =
            new(() =>
            {
                callCount++;
                return completion.Task;
            });

        try
        {
            MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
                new Settings(),
                (_, window) =>
                {
                    ContextMenu menu = (ContextMenu)window.FindResource("treeViewPlaylistRootContextMenu");
                    MenuItem reload = menu.Items.OfType<MenuItem>().Last();

                    RoutedEventArgs args = RaiseMenuClick(reload);

                    Assert.IsTrue(args.Handled);
                    Assert.AreEqual(1, callCount);
                    Assert.IsFalse(completion.Task.IsCompleted);

                    completion.SetResult(null);
                    TestUiDispatcherHost.Drain();
                },
                playlistWorkspaceTerminals: CreateTerminals(tablesReload: reloadTerminal));
        }
        finally
        {
            completion.TrySetResult(null);
            TestUiDispatcherHost.Drain();
        }
    }

    [TestMethod]
    public void PlaylistTableListImportMenu_StaysOpenOnlyForLeafItemsAndUsesQueue()
    {
        var collectionCalls = new List<BMSTableSimple>();
        var builtInTags = new List<string>();
        BMSTableSimpleCategorized parent = new()
        {
            name = "Collection group",
            Children =
            [
                new BMSTableSimpleCategorized
                {
                    name = "Collection leaf",
                    url = new Uri("https://example.invalid/collection.json")
                }
            ]
        };
        MainWindowPlaylistCollectionImportTerminal importTerminal = new(
            source =>
            {
                collectionCalls.Add(source);
                return true;
            },
            rawTag =>
            {
                builtInTags.Add(rawTag);
                return true;
            });

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            new Settings(),
            (_, window) =>
            {
                ContextMenu rootMenu = (ContextMenu)window.FindResource("treeViewPlaylistRootContextMenu");
                using HwndSource menuHost = new(new HwndSourceParameters("PlaylistImportMenuHierarchyTest")
                {
                    Width = 640,
                    Height = 480,
                    PositionX = 0,
                    PositionY = 0
                });
                menuHost.RootVisual = rootMenu;
                MenuItem collectionMenu = FindMenuItem(
                    rootMenu,
                    "treeViewPlaylistRootContextMenuItemLoadPlaylistCollection");
                collectionMenu.ItemsSource = new List<BMSTableSimpleCategorized> { parent };
                MaterializeMenuItems(collectionMenu);

                MenuItem parentItem = GetOrGenerateMenuItem(collectionMenu, parent);
                Assert.IsNotNull(parentItem);
                Assert.IsFalse(parentItem.StaysOpenOnClick);
                parentItem.IsSubmenuOpen = true;
                MaterializeMenuItems(parentItem);

                BMSTableSimple leaf = parent.Children.Single();
                MenuItem leafItem = GetOrGenerateMenuItem(parentItem, leaf);
                Assert.IsNotNull(leafItem);
                Assert.IsTrue(leafItem.StaysOpenOnClick);

                RoutedEventArgs collectionArgs = RaiseMenuClick(leafItem);
                Assert.IsTrue(collectionArgs.Handled);
                Assert.AreEqual(1, collectionCalls.Count);
                Assert.AreSame(leaf, collectionCalls[0]);

                MenuItem builtInMenu = FindMenuItem(
                    rootMenu,
                    "treeViewPlaylistRootContextMenuItemLoadWalkureTable");
                MenuItem builtInLeaf = builtInMenu.Items
                    .OfType<MenuItem>()
                    .First(item => item.Tag is string);
                string rawTag = (string)builtInLeaf.Tag;

                RoutedEventArgs builtInArgs = RaiseMenuClick(builtInLeaf);
                Assert.IsTrue(builtInArgs.Handled);
                Assert.AreEqual(1, builtInTags.Count);
                Assert.AreEqual(rawTag, builtInTags[0]);
            },
            playlistWorkspaceTerminals: CreateTerminals(collectionImport: importTerminal));
    }

    [TestMethod]
    public void PlaylistUrlBulkImport_IsOwnedByWorkspaceAndShellForwarded()
    {
        var singleCompletion = NewCompletion();
        var bulkCompletion = NewCompletion();
        var singleUrls = new List<Uri>();
        var availabilityCalls = new List<(object Context, IReadOnlyList<object> Rows)>();
        var bulkCalls = new List<(IReadOnlyList<object> Rows, bool IsDiff)>();
        var externalLookupCalls = new List<IReadOnlyList<object>>();
        int expansionCalls = 0;
        PlaylistUrlInstallTreeExpansionEventSourceFake expansionEventSource = new();
        PlaylistUrlInstallTreeExpansionOwnerFake expansionOwner = new(expansionEventSource);
        BMSTable playlistTable = new() { name = "URL playlist" };
        PlaylistDetailRow firstRow = CreatePlaylistUrlRow(
            playlistTable,
            "33333333333333333333333333333333",
            "https://example.invalid/main-first.zip",
            "https://example.invalid/diff-first.zip");
        PlaylistDetailRow secondRow = CreatePlaylistUrlRow(
            playlistTable,
            "44444444444444444444444444444444",
            "https://example.invalid/main-second.zip",
            "https://example.invalid/diff-second.zip");
        MainWindowPlaylistUrlAcquisitionTerminal urlAcquisition = new(
            url =>
            {
                singleUrls.Add(url);
                return singleCompletion.Task;
            },
            (rows, isDiff) =>
            {
                bulkCalls.Add((rows, isDiff));
                return bulkCompletion.Task;
            },
            rows =>
            {
                externalLookupCalls.Add(rows);
                return Task.CompletedTask;
            },
            (contextRow, rows) =>
            {
                availabilityCalls.Add((contextRow, rows));
                return new PlaylistUrlContextMenuAvailability(
                    isPlaylistContext: true,
                    isBulkContext: rows.Count > 1,
                    canOpenUrl: true,
                    canOpenDiffUrl: true,
                    canFindExternalPackage: true);
            });
        MainWindowPlaylistUrlInstallTreeExpansionTerminal expansion = new(() =>
        {
            Assert.IsTrue(bulkCompletion.Task.IsCompleted);
            expansionCalls++;
        });

        try
        {
            MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
                new Settings(),
                (viewModel, window) =>
                {
                    CustomTableView table = (CustomTableView)window.FindName("customTableView");
                    table.ItemsSource = new List<object> { firstRow, secondRow };
                    table.SelectRowsByPredicate(_ => true);
                    viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.FolderFilterSelected);

                    ContextMenu menu = (ContextMenu)window.FindResource("tableContextMenu");
                    menu.PlacementTarget = new FrameworkElement { DataContext = firstRow };
                    OpenContextMenu(menu);

                    Assert.AreEqual(1, availabilityCalls.Count);
                    Assert.AreSame(firstRow, availabilityCalls[0].Context);
                    Assert.AreEqual(2, availabilityCalls[0].Rows.Count);
                    Assert.AreSame(firstRow, availabilityCalls[0].Rows[0]);
                    Assert.AreSame(secondRow, availabilityCalls[0].Rows[1]);

                    MenuItem openUrl = FindMenuItem(menu, "tableContextMenuItemOpenURL");
                    RoutedEventArgs openArgs = RaiseMenuClick(openUrl);
                    Assert.IsTrue(openArgs.Handled);
                    Assert.AreEqual(1, bulkCalls.Count);
                    Assert.IsFalse(bulkCalls[0].IsDiff);
                    Assert.AreEqual(2, bulkCalls[0].Rows.Count);
                    Assert.AreSame(firstRow, bulkCalls[0].Rows[0]);
                    Assert.AreSame(secondRow, bulkCalls[0].Rows[1]);
                    Assert.IsFalse(bulkCompletion.Task.IsCompleted);
                    Assert.AreEqual(0, expansionCalls);

                    bulkCompletion.SetResult(null);
                    TestUiDispatcherHost.Drain();

                    RoutedEventArgs diffArgs = RaiseMenuClick(
                        FindMenuItem(menu, "tableContextMenuItemOpenURLdiff"));
                    Assert.IsTrue(diffArgs.Handled);
                    Assert.AreEqual(2, bulkCalls.Count);
                    Assert.IsTrue(bulkCalls[1].IsDiff);
                    Assert.AreEqual(2, bulkCalls[1].Rows.Count);
                    Assert.AreSame(firstRow, bulkCalls[1].Rows[0]);
                    Assert.AreSame(secondRow, bulkCalls[1].Rows[1]);

                    RoutedEventArgs lookupArgs = RaiseMenuClick(
                        FindMenuItem(menu, "tableContextMenuItemFindExternalPackage"));
                    Assert.IsTrue(lookupArgs.Handled);
                    Assert.AreEqual(1, externalLookupCalls.Count);
                    Assert.AreEqual(2, externalLookupCalls[0].Count);
                    Assert.AreSame(firstRow, externalLookupCalls[0][0]);
                    Assert.AreSame(secondRow, externalLookupCalls[0][1]);

                    expansionOwner.Publish(PlaylistUrlInstallOwnerOutcome.Succeeded);
                    Assert.AreEqual(1, expansionCalls);

                    table.ItemsSource = new List<object> { firstRow };
                    table.SelectRowsByPredicate(_ => true);
                    table.Width = 120d;
                    table.Height = 80d;
                    table.HeaderHeight = 0d;
                    table.RowHeight = 60d;
                    table.Columns =
                    [
                        new CustomTableColumn(
                            "Url1",
                            "URL1",
                            layout: null,
                            fallbackOrder: 0,
                            sortMemberPath: null,
                            alignment: TextAlignment.Center,
                            textSelector: _ => "download",
                            minWidth: 40,
                            maxWidth: 40,
                            canResize: false,
                            cellKind: CustomTableCellKind.DownloadIcon)
                    ];
                    using HwndSource visualHost = new(new HwndSourceParameters("PlaylistUrlCellRouteTest")
                    {
                        Width = 1000,
                        Height = 700,
                        PositionX = 0,
                        PositionY = 0
                    });
                    visualHost.RootVisual = (System.Windows.Media.Visual)window.Content;
                    window.Width = 1000d;
                    window.Height = 700d;
                    window.Measure(new Size(window.Width, window.Height));
                    window.Arrange(new Rect(0d, 0d, window.Width, window.Height));
                    window.UpdateLayout();
                    table.Measure(new Size(table.Width, table.Height));
                    table.Arrange(new Rect(0d, 0d, table.Width, table.Height));
                    table.UpdateLayout();

                    Assert.AreEqual(CustomTableHitKind.Cell, table.HitTestTable(new Point(20d, 30d)).Kind);
                    RunWithWindowCursor(window, table, 20d, 30d, () =>
                    {
                        Point actualPoint = Mouse.GetPosition(table);
                        Assert.AreEqual(CustomTableHitKind.Cell, table.HitTestTable(actualPoint).Kind, $"cursor={actualPoint}");
                        RaiseCellClick(table);
                    });
                    Assert.AreEqual(1, singleUrls.Count);
                    Assert.AreEqual(firstRow.Url, singleUrls[0]);
                    Assert.IsFalse(singleCompletion.Task.IsCompleted);
                    singleCompletion.SetResult(null);
                    TestUiDispatcherHost.Drain();
                },
                playlistWorkspaceTerminals: CreateTerminals(
                    urlAcquisition: urlAcquisition,
                    urlInstallTreeExpansionEventSource: expansionEventSource,
                    urlInstallTreeExpansion: expansion));

            Assert.AreEqual(0, expansionEventSource.SubscriberCount);
            expansionOwner.Publish(PlaylistUrlInstallOwnerOutcome.Succeeded);
            Assert.AreEqual(1, expansionCalls);
        }
        finally
        {
            singleCompletion.TrySetResult(null);
            bulkCompletion.TrySetResult(null);
            TestUiDispatcherHost.Drain();
        }
    }

    [TestMethod]
    public void PlaylistUrlInstallTreeExpansion_UsesSuccessfulOwnerEventAndUnsubscribes()
    {
        int expansionCalls = 0;
        PlaylistUrlInstallTreeExpansionEventSourceFake expansionEventSource = new();
        PlaylistUrlInstallTreeExpansionOwnerFake expansionOwner = new(expansionEventSource);
        MainWindowPlaylistUrlInstallTreeExpansionTerminal expansion = new(() => expansionCalls++);

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            new Settings(),
            (_, _) =>
            {
                Assert.AreEqual(1, expansionEventSource.SubscriberCount);

                expansionOwner.Publish(PlaylistUrlInstallOwnerOutcome.Rejected);
                expansionOwner.Publish(PlaylistUrlInstallOwnerOutcome.Faulted);
                expansionOwner.Publish(PlaylistUrlInstallOwnerOutcome.Canceled);
                Assert.AreEqual(0, expansionEventSource.PublishCount);
                Assert.AreEqual(0, expansionCalls);

                expansionOwner.Publish(PlaylistUrlInstallOwnerOutcome.Succeeded);
                Assert.AreEqual(1, expansionEventSource.PublishCount);
                Assert.AreEqual(1, expansionCalls);
            },
            playlistWorkspaceTerminals: CreateTerminals(
                urlInstallTreeExpansionEventSource: expansionEventSource,
                urlInstallTreeExpansion: expansion));

        Assert.AreEqual(0, expansionEventSource.SubscriberCount);
        expansionOwner.Publish(PlaylistUrlInstallOwnerOutcome.Succeeded);
        Assert.AreEqual(2, expansionEventSource.PublishCount);
        Assert.AreEqual(1, expansionCalls);
    }

    [TestMethod]
    public void PlaylistEntryRemovalRoutesThroughWorkspaceOwner()
    {
        var completion = NewCompletion();
        IReadOnlyList<object> capturedRows = null;
        int callCount = 0;
        BMSTable playlistTable = new() { name = "Playlist" };
        PlaylistDetailRow firstRow = CreatePlaylistRow(
            playlistTable,
            "11111111111111111111111111111111",
            @"C:\wave6e-playlist\first.bms");
        PlaylistDetailRow secondRow = CreatePlaylistRow(
            playlistTable,
            "22222222222222222222222222222222",
            @"C:\wave6e-playlist\second.bms");
        MainWindowPlaylistEntryRemovalTerminal entryRemoval = new(
            rows =>
            {
                callCount++;
                capturedRows = rows;
                return completion.Task;
            });

        try
        {
            MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
                new Settings(),
                (viewModel, window) =>
                {
                    CustomTableView table = (CustomTableView)window.FindName("customTableView");
                    table.ItemsSource = new List<object> { firstRow, secondRow };
                    table.SelectRowsByPredicate(row => ReferenceEquals(row, firstRow) || ReferenceEquals(row, secondRow));
                    viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.FolderFilterSelected);

                    ContextMenu menu = (ContextMenu)window.FindResource("tableContextMenu");
                    menu.PlacementTarget = new FrameworkElement { DataContext = firstRow };
                    MenuItem removeEntry = FindMenuItem(menu, "tableContextMenuItemDeleteEntry");
                    OpenContextMenu(menu);

                    RoutedEventArgs args = RaiseMenuClick(removeEntry);

                    Assert.IsTrue(args.Handled);
                    Assert.AreEqual(1, callCount);
                    Assert.IsFalse(completion.Task.IsCompleted);
                    Assert.IsNotNull(capturedRows);
                    Assert.AreEqual(2, capturedRows.Count);
                    Assert.AreSame(firstRow, capturedRows[0]);
                    Assert.AreSame(secondRow, capturedRows[1]);

                    completion.SetResult(null);
                    TestUiDispatcherHost.Drain();
                },
                playlistWorkspaceTerminals: CreateTerminals(entryRemoval: entryRemoval));
        }
        finally
        {
            completion.TrySetResult(null);
            TestUiDispatcherHost.Drain();
        }
    }

    [TestMethod]
    public void PlaylistOverwriteLevel_RoutesThroughWorkflowOwner()
    {
        var completion = NewCompletion();
        BMSTable capturedTable = null;
        int callCount = 0;
        BMSTable table = new() { name = "Overwrite target" };
        MainWindowPlaylistTableLevelOverwriteTerminal overwrite = new(
            captured =>
            {
                callCount++;
                capturedTable = captured;
                return completion.Task;
            });

        try
        {
            MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
                new Settings(),
                (_, window) =>
                {
                    ContextMenu menu = (ContextMenu)window.FindResource("treeViewPlaylistTableContextMenu");
                    menu.PlacementTarget = new TreeViewItem { DataContext = table };
                    menu.DataContext = table;
                    OpenContextMenu(menu);

                    MenuItem overwriteLevel = FindMenuItem(
                        menu,
                        "treeViewPlaylistTableContextMenuItemOverwriteLevel");
                    overwriteLevel.DataContext = table;
                    RoutedEventArgs args = RaiseMenuClick(overwriteLevel);

                    Assert.IsTrue(args.Handled);
                    Assert.AreEqual(1, callCount);
                    Assert.AreSame(table, capturedTable);
                    Assert.IsFalse(completion.Task.IsCompleted);

                    completion.SetResult(null);
                    TestUiDispatcherHost.Drain();
                },
                playlistWorkspaceTerminals: CreateTerminals(tableLevelOverwrite: overwrite));
        }
        finally
        {
            completion.TrySetResult(null);
            TestUiDispatcherHost.Drain();
        }
    }

    [TestMethod]
    public void PlaylistTableRemoval_RoutesThroughWorkflowOwner()
    {
        RunPlaylistTableRemovalRejectionScenario();
        RunPlaylistTableRemovalSiblingFallbackScenario();
        RunPlaylistTableRemovalEmptyRootScenario();
    }

    private static void RunPlaylistTableRemovalRejectionScenario()
    {
        BMSTable firstTable = new() { name = "First" };
        BMSTable secondTable = new() { name = "Second" };
        var completion = NewCompletion();
        int callbackInvocationCount = 0;
        int callCount = 0;
        BMSTable capturedTable = null;
        MainWindowPlaylistTableRemovalTerminal removal = new(
            (table, _) =>
            {
                callCount++;
                capturedTable = table;
                return completion.Task;
            });

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            new Settings(),
            (_, window) =>
            {
                try
                {
                    (TreeViewItem playlistRoot, MenuItem removeTable) =
                        PreparePlaylistTableRemovalContext(window, firstTable, secondTable);

                    RoutedEventArgs args = RaiseMenuClick(removeTable);
                    Assert.IsTrue(args.Handled);
                    Assert.AreEqual(1, callCount);
                    Assert.AreSame(firstTable, capturedTable);
                    Assert.AreEqual(0, callbackInvocationCount);
                    Assert.IsTrue(playlistRoot.IsSelected);
                    Assert.IsFalse(completion.Task.IsCompleted);

                    QueueCompletion(completion);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        completion.Task,
                        "playlist table removal rejection");
                    Assert.IsTrue(playlistRoot.IsSelected);
                    Assert.AreEqual(2, playlistRoot.Items.Count);
                }
                finally
                {
                    completion.TrySetResult(null);
                }
            },
            playlistWorkspaceTerminals: CreateTerminals(tableRemoval: removal));
    }

    private static void RunPlaylistTableRemovalSiblingFallbackScenario()
    {
        BMSTable firstTable = new() { name = "First" };
        BMSTable secondTable = new() { name = "Second" };
        var completion = NewCompletion();
        var selectionCompletion = NewCompletion();
        int callbackInvocationCount = 0;
        var callbacks = new List<System.Action>();
        var capturedTables = new List<BMSTable>();
        MainWindowPlaylistTableRemovalTerminal removal = new(
            (table, applySelectionBeforeMutation) =>
            {
                capturedTables.Add(table);
                callbacks.Add(applySelectionBeforeMutation);
                callbackInvocationCount++;
                applySelectionBeforeMutation();
                return completion.Task;
            });

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            new Settings(),
            (viewModel, window) =>
            {
                EventHandler<PlaylistTreeSelectionActivatedEventArgs> selectionActivated = (_, args) =>
                {
                    if (!args.IsSummary && args.Detail != null && ReferenceEquals(args.Detail.Table, secondTable))
                    {
                        selectionCompletion.TrySetResult(null);
                    }
                };
                viewModel.PlaylistWorkspace.TreeSelectionActivated += selectionActivated;
                try
                {
                    (TreeViewItem playlistRoot, MenuItem removeTable) =
                        PreparePlaylistTableRemovalContext(window, firstTable, secondTable);

                    RoutedEventArgs args = RaiseMenuClick(removeTable);
                    Assert.IsTrue(args.Handled);
                    Assert.AreEqual(1, capturedTables.Count);
                    Assert.AreSame(firstTable, capturedTables[0]);
                    Assert.AreEqual(1, callbacks.Count);
                    Assert.AreEqual(1, callbackInvocationCount);
                    Assert.IsFalse(completion.Task.IsCompleted);

                    QueueCompletion(completion);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        Task.WhenAll(completion.Task, selectionCompletion.Task),
                        "playlist table removal sibling fallback");

                    TreeViewItem sibling = playlistRoot.ItemContainerGenerator.ContainerFromItem(secondTable) as TreeViewItem;
                    Assert.IsNotNull(sibling);
                    Assert.IsTrue(sibling.IsSelected);
                    Assert.AreSame(secondTable, sibling.DataContext);
                }
                finally
                {
                    viewModel.PlaylistWorkspace.TreeSelectionActivated -= selectionActivated;
                    completion.TrySetResult(null);
                }
            },
            playlistWorkspaceTerminals: CreateTerminals(tableRemoval: removal));
    }

    private static void RunPlaylistTableRemovalEmptyRootScenario()
    {
        BMSTable firstTable = new() { name = "First" };
        var completion = NewCompletion();
        var selectionCompletion = NewCompletion();
        PlaylistTreeSelectionActivatedEventArgs emptyRootSelection = null;
        int callbackInvocationCount = 0;
        var callbacks = new List<System.Action>();
        var capturedTables = new List<BMSTable>();
        MainWindowPlaylistTableRemovalTerminal removal = new(
            (table, applySelectionBeforeMutation) =>
            {
                capturedTables.Add(table);
                callbacks.Add(applySelectionBeforeMutation);
                callbackInvocationCount++;
                applySelectionBeforeMutation();
                return completion.Task;
            });

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            new Settings(),
            (viewModel, window) =>
            {
                EventHandler<PlaylistTreeSelectionActivatedEventArgs> selectionActivated = (_, args) =>
                {
                    if (!args.IsSummary && args.Detail != null && args.Detail.Table == null)
                    {
                        emptyRootSelection = args;
                        selectionCompletion.TrySetResult(null);
                    }
                };
                viewModel.PlaylistWorkspace.TreeSelectionActivated += selectionActivated;
                try
                {
                    (TreeViewItem playlistRoot, MenuItem removeTable) =
                        PreparePlaylistTableRemovalContext(window, firstTable);
                    playlistRoot.Items.Clear();
                    playlistRoot.IsSelected = true;
                    TestUiDispatcherHost.Drain();

                    RoutedEventArgs args = RaiseMenuClick(removeTable);
                    Assert.IsTrue(args.Handled);
                    Assert.AreEqual(1, capturedTables.Count);
                    Assert.AreSame(firstTable, capturedTables[0]);
                    Assert.AreEqual(1, callbacks.Count);
                    Assert.AreEqual(1, callbackInvocationCount);
                    Assert.IsFalse(completion.Task.IsCompleted);

                    QueueCompletion(completion);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        Task.WhenAll(completion.Task, selectionCompletion.Task),
                        "playlist table removal empty-root detail selection");

                    Assert.IsNotNull(emptyRootSelection);
                    Assert.IsFalse(emptyRootSelection.IsSummary);
                    Assert.IsNotNull(emptyRootSelection.Detail);
                    Assert.IsNull(emptyRootSelection.Detail.Table);
                    Assert.IsTrue(playlistRoot.IsSelected);
                    Assert.AreEqual(0, playlistRoot.Items.Count);
                    PlaylistDetailSelection emptyDetailSelection =
                        viewModel.PlaylistWorkspace.CapturePlaylistDetailSelection();
                    Assert.IsNotNull(emptyDetailSelection);
                    Assert.IsNull(emptyDetailSelection.Table);
                }
                finally
                {
                    viewModel.PlaylistWorkspace.TreeSelectionActivated -= selectionActivated;
                    completion.TrySetResult(null);
                }
            },
            playlistWorkspaceTerminals: CreateTerminals(tableRemoval: removal));
    }

    private static (TreeViewItem PlaylistRoot, MenuItem RemoveTable) PreparePlaylistTableRemovalContext(
        MainWindow window,
        params BMSTable[] tables)
    {
        TreeViewItem playlistRoot = (TreeViewItem)window.FindName("treeViewItemPlaylist");
        playlistRoot.ItemsSource = null;
        playlistRoot.Items.Clear();
        foreach (BMSTable table in tables)
        {
            playlistRoot.Items.Add(table);
        }
        MaterializeTreeItems(playlistRoot);
        playlistRoot.IsSelected = true;
        TestUiDispatcherHost.Drain();

        ContextMenu menu = (ContextMenu)window.FindResource("treeViewPlaylistTableContextMenu");
        BMSTable firstTable = tables[0];
        menu.PlacementTarget = new TreeViewItem { DataContext = firstTable };
        menu.DataContext = firstTable;
        OpenContextMenu(menu);
        MenuItem removeTable = FindMenuItem(
            menu,
            "treeViewPlaylistTableContextMenuItemRemoveTable");
        removeTable.DataContext = firstTable;
        return (playlistRoot, removeTable);
    }

    private static void QueueCompletion(TaskCompletionSource<object?> completion)
    {
        TestUiDispatcherHost.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Normal,
            new Action(() => completion.TrySetResult(null)));
    }

    private static MainWindowPlaylistWorkspaceTerminals CreateTerminals(
        MainWindowPlaylistEntryRemovalTerminal entryRemoval = null,
        MainWindowPlaylistTableLevelOverwriteTerminal tableLevelOverwrite = null,
        MainWindowPlaylistTableRemovalTerminal tableRemoval = null,
        MainWindowPlaylistCollectionImportTerminal collectionImport = null,
        MainWindowPlaylistUrlAcquisitionTerminal urlAcquisition = null,
        IMainWindowPlaylistUrlInstallTreeExpansionEventSource urlInstallTreeExpansionEventSource = null,
        MainWindowPlaylistUrlInstallTreeExpansionTerminal urlInstallTreeExpansion = null,
        MainWindowPlaylistTablesReloadTerminal tablesReload = null)
    {
        return new MainWindowPlaylistWorkspaceTerminals(
            entryRemoval ?? new MainWindowPlaylistEntryRemovalTerminal(_ => Task.CompletedTask),
            tableLevelOverwrite ?? new MainWindowPlaylistTableLevelOverwriteTerminal(_ => Task.CompletedTask),
            tableRemoval ?? new MainWindowPlaylistTableRemovalTerminal((_, _) => Task.CompletedTask),
            collectionImport ?? new MainWindowPlaylistCollectionImportTerminal((_) => false, (_) => false),
            urlAcquisition ?? new MainWindowPlaylistUrlAcquisitionTerminal(
                (_) => Task.CompletedTask,
                (_, _) => Task.CompletedTask,
                (_) => Task.CompletedTask,
                (_, _) => new PlaylistUrlContextMenuAvailability(
                    isPlaylistContext: false,
                    isBulkContext: false,
                    canOpenUrl: false,
                    canOpenDiffUrl: false,
                    canFindExternalPackage: false)),
            urlInstallTreeExpansionEventSource ?? new PlaylistUrlInstallTreeExpansionEventSourceFake(),
            urlInstallTreeExpansion ?? new MainWindowPlaylistUrlInstallTreeExpansionTerminal(() => { }),
            tablesReload ?? new MainWindowPlaylistTablesReloadTerminal(() => Task.CompletedTask));
    }

    private enum PlaylistUrlInstallOwnerOutcome
    {
        Succeeded,
        Rejected,
        Faulted,
        Canceled
    }

    private sealed class PlaylistUrlInstallTreeExpansionOwnerFake
    {
        private readonly PlaylistUrlInstallTreeExpansionEventSourceFake eventSource;

        internal PlaylistUrlInstallTreeExpansionOwnerFake(
            PlaylistUrlInstallTreeExpansionEventSourceFake eventSource)
        {
            this.eventSource = eventSource ?? throw new ArgumentNullException(nameof(eventSource));
        }

        internal void Publish(PlaylistUrlInstallOwnerOutcome outcome)
        {
            if (outcome == PlaylistUrlInstallOwnerOutcome.Succeeded)
            {
                eventSource.Publish();
            }
        }
    }

    private sealed class PlaylistUrlInstallTreeExpansionEventSourceFake
        : IMainWindowPlaylistUrlInstallTreeExpansionEventSource
    {
        private Action handlers;

        internal int SubscriberCount => handlers?.GetInvocationList().Length ?? 0;

        internal int PublishCount { get; private set; }

        public void Subscribe(Action handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            handlers += handler;
        }

        public void Unsubscribe(Action handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            handlers -= handler;
        }

        internal void Publish()
        {
            PublishCount++;
            handlers?.Invoke();
        }
    }

    private static PlaylistDetailRow CreatePlaylistRow(BMSTable table, string md5, string path)
    {
        BMSFile file = new() { hash = md5, path = path, title = md5 };
        BMSTableEntry entry = new() { md5 = md5, parent = table };
        return new PlaylistDetailSourceRow(
            entry,
            ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false, includeResourceReferences: false))
            .CreateViewRow();
    }

    private static PlaylistDetailRow CreatePlaylistUrlRow(
        BMSTable table,
        string md5,
        string url,
        string urlDiff)
    {
        BMSFile file = new()
        {
            hash = md5,
            path = $@"C:\wave6e-playlist-url\{md5}.bms",
            title = md5
        };
        BMSTableEntry entry = new()
        {
            md5 = md5,
            parent = table,
            Url = new Uri(url),
            Url_diff = new Uri(urlDiff)
        };
        return new PlaylistDetailSourceRow(
            entry,
            ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false, includeResourceReferences: false))
            .CreateViewRow();
    }

    private static TaskCompletionSource<object?> NewCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void MaterializeTreeItems(TreeViewItem root)
    {
        root.ApplyTemplate();
        root.Measure(new Size(640d, 480d));
        root.Arrange(new Rect(0d, 0d, 640d, 480d));
        root.UpdateLayout();
        TestUiDispatcherHost.Drain();
    }

    private static void MaterializeMenuItems(ItemsControl menu)
    {
        menu.ApplyTemplate();
        menu.Measure(new Size(640d, 480d));
        menu.Arrange(new Rect(0d, 0d, 640d, 480d));
        menu.UpdateLayout();
        TestUiDispatcherHost.Drain();
    }

    private static MenuItem GetOrGenerateMenuItem(ItemsControl owner, object item)
    {
        MenuItem generated = owner.ItemContainerGenerator.ContainerFromItem(item) as MenuItem;
        if (generated != null)
        {
            return generated;
        }

        int index = owner.Items.IndexOf(item);
        Assert.IsTrue(index >= 0, $"The menu item {item} was not present in its ItemsSource.");
        IItemContainerGenerator generator = owner.ItemContainerGenerator;
        using (generator.StartAt(
            generator.GeneratorPositionFromIndex(index),
            GeneratorDirection.Forward,
            allowStartAtRealizedItem: true))
        {
            DependencyObject candidate = generator.GenerateNext(out bool newlyRealized);
            if (newlyRealized)
            {
                generator.PrepareItemContainer(candidate);
            }
        }

        return owner.ItemContainerGenerator.ContainerFromItem(item) as MenuItem;
    }

    private static void OpenContextMenu(ContextMenu menu)
    {
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, menu));
        TestUiDispatcherHost.Drain();
    }

    private static void RaiseCellClick(CustomTableView table)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
        };
        table.RaiseEvent(args);
    }

    private static void RunWithWindowCursor(
        MainWindow window,
        CustomTableView table,
        double x,
        double y,
        Action action)
    {
        using TestProcessGlobalCursorScope cursorScope = TestProcessGlobalCursorScope.Enter();
        GetCursorPos(out NativePoint originalPosition);
        try
        {
            table.Focus();
            Assert.IsTrue(Mouse.Capture(table));
            Point tableOrigin = table.PointToScreen(new Point(0d, 0d));
            Point screenPoint = new(tableOrigin.X + x, tableOrigin.Y + y);
            Assert.IsTrue(SetCursorPos((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y)));
            table.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() => { }));
            Point adjustedOrigin = table.PointToScreen(new Point(0d, 0d));
            Assert.IsTrue(SetCursorPos(
                (int)Math.Round(adjustedOrigin.X + x),
                (int)Math.Round(adjustedOrigin.Y + y)));
            table.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() => { }));
            if (PresentationSource.FromVisual((System.Windows.Media.Visual)window.Content) is HwndSource source)
            {
                Point screenPointForMessage = new(adjustedOrigin.X + x, adjustedOrigin.Y + y);
                Point rootPoint = ((System.Windows.Media.Visual)window.Content).PointFromScreen(screenPointForMessage);
                SendMessage(
                    source.Handle,
                    0x0200,
                    IntPtr.Zero,
                    MakeLParam((int)Math.Round(rootPoint.X), (int)Math.Round(rootPoint.Y)));
            }
            table.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() => { }));
            action();
        }
        finally
        {
            Mouse.Capture(null);
            SetCursorPos(originalPosition.X, originalPosition.Y);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr windowHandle, int message, IntPtr wParam, IntPtr lParam);

    private static IntPtr MakeLParam(int low, int high)
        => (IntPtr)((high << 16) | (low & 0xffff));

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private static RoutedEventArgs RaiseMenuClick(MenuItem item)
    {
        var args = new RoutedEventArgs(MenuItem.ClickEvent, item);
        item.RaiseEvent(args);
        return args;
    }

    private static MenuItem FindMenuItem(ItemsControl root, string name)
    {
        MenuItem result = FindMenuItemOrNull(root, name);
        if (result != null)
        {
            return result;
        }
        throw new AssertFailedException($"Menu item '{name}' was not found.");
    }

    private static MenuItem FindMenuItemOrNull(ItemsControl root, string name)
    {
        foreach (MenuItem item in root.Items.OfType<MenuItem>())
        {
            if (item.Name == name)
            {
                return item;
            }
            if (item.Items.Count > 0)
            {
                MenuItem nested = FindMenuItemOrNull(item, name);
                if (nested != null)
                {
                    return nested;
                }
            }
        }
        return null;
    }
}
