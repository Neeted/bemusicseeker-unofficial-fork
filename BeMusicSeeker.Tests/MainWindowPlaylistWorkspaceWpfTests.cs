using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MainWindowPlaylistWorkspaceWpfTests
{
    [TestMethod]
    public void MainWindowPlaylistDialogs_UseOwnedNativeModalLifetimeAndCleanup()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            ActualMainWindowFixture fixture = CreateActualMainWindowFixture(windowTest);
            try
            {
                CustomTableView summary = (CustomTableView)fixture.Window.FindName("customTablePlaylistSummary");
                PlaylistSummaryRow row = CreatePlaylistSummaryRow(fixture.Table);
                summary.ItemsSource = new List<PlaylistSummaryRow> { row };
                summary.SelectRowsByPredicate(_ => true);
                TestUiDispatcherHost.Drain();

                ModalObservation<PlaylistPropertyDialog> first = OpenPropertyDialog(
                    fixture.Window,
                    summary,
                    row,
                    "MainWindowPlaylistWorkspaceWpfTests.property-open",
                    (dialog, _) =>
                    {
                        FrameworkElement navigation = (FrameworkElement)dialog.FindName("propertyNavigation")
                            ?? throw new AssertFailedException("Playlist property navigation was not materialized.");
                        FrameworkElement contentHost = (FrameworkElement)dialog.FindName("propertyContent")
                            ?? throw new AssertFailedException("Playlist property content host was not materialized.");
                        PropertyNavigationObservation navigationState = ObservePropertyNavigation(navigation, contentHost);
                        AssertSelectedPropertyCategory(navigationState, PropertyNavigationCategory.General);
                        Assert.AreSame(
                            navigationState.ItemsByCategory[PropertyNavigationCategory.General],
                            navigationState.InitiallySelectedItem,
                            "The property dialog must capture its initial General provider before category navigation.");
                        Assert.AreEqual(
                            PropertyNavigationCategory.General,
                            IdentifyVisiblePropertyCategory(contentHost));
                        navigationState.ItemsByCategory[PropertyNavigationCategory.Folder].Provider.Select();
                        TestUiDispatcherHost.Drain();
                        AssertSelectedPropertyCategory(navigationState, PropertyNavigationCategory.Folder);
                        Assert.AreEqual(PropertyNavigationCategory.Folder, IdentifyVisiblePropertyCategory(contentHost));
                        RaiseButtonClick(FindAutomationButton(dialog, "PlaylistPropertyCancel"));
                    });
                Assert.AreSame(fixture.Window, first.Owner);
                Assert.IsFalse(first.OwnerEnabled);
                Assert.IsInstanceOfType(first.DataContext, typeof(PlaylistPropertyDialogViewModel));
                Assert.AreEqual(1, first.DataContextDetachCount);
                Assert.IsNull(fixture.ViewModel.PlaylistWorkspace.ActivePropertyDialog);
                Assert.AreSame(fixture.Table, row.TableRef);
                fixture.ModalPreparation.AssertLatest(first.Window, expectedCount: 1);

                ModalObservation<PlaylistPropertyDialog> reopened = OpenPropertyDialog(
                    fixture.Window,
                    summary,
                    row,
                    "MainWindowPlaylistWorkspaceWpfTests.property-reopen",
                    (dialog, _) =>
                    {
                        FrameworkElement reopenedNavigation = (FrameworkElement)dialog.FindName("propertyNavigation")
                            ?? throw new AssertFailedException("Reopened playlist property navigation was not materialized.");
                        FrameworkElement reopenedContent = (FrameworkElement)dialog.FindName("propertyContent")
                            ?? throw new AssertFailedException("Reopened playlist property content host was not materialized.");
                        PropertyNavigationObservation reopenedNavigationState = ObservePropertyNavigation(reopenedNavigation, reopenedContent);
                        AssertSelectedPropertyCategory(
                            reopenedNavigationState,
                            PropertyNavigationCategory.General,
                            "A reopened property dialog must select General without restoring navigation state.");
                        Assert.AreSame(
                            reopenedNavigationState.ItemsByCategory[PropertyNavigationCategory.General],
                            reopenedNavigationState.InitiallySelectedItem,
                            "A reopened property dialog must capture its newly selected General provider before navigation mutations.");
                        RaiseButtonClick(FindAutomationButton(dialog, "PlaylistPropertyCancel"));
                    });
                Assert.AreSame(fixture.Window, reopened.Owner);
                Assert.IsFalse(reopened.OwnerEnabled);
                Assert.AreNotSame(first.DataContext, reopened.DataContext);
                Assert.AreEqual(1, reopened.DataContextDetachCount);
                Assert.IsNull(fixture.ViewModel.PlaylistWorkspace.ActivePropertyDialog);
                fixture.ModalPreparation.AssertLatest(reopened.Window, expectedCount: 2);

                ModalObservation<PlaylistSummaryBulkEditDialog> bulk = OpenBulkDialog(
                    fixture.Window,
                    summary,
                    row,
                    "MainWindowPlaylistWorkspaceWpfTests.bulk-open");
                Assert.AreSame(fixture.Window, bulk.Owner);
                Assert.IsFalse(bulk.OwnerEnabled);
                Assert.IsInstanceOfType(
                    bulk.DataContext,
                    typeof(PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel));
                Assert.IsTrue(bulk.VisibleAfterApply);
                Assert.IsTrue(fixture.Table.is_bmt_output);
                Assert.AreEqual(1, bulk.DataContextDetachCount);
                Assert.IsNull(fixture.ViewModel.PlaylistWorkspace.ActiveSummaryBulkEditDialog);
                fixture.ModalPreparation.AssertLatest(bulk.Window, expectedCount: 3);
            }
            finally
            {
                fixture.Close();
            }
        });
    }

    [TestMethod]
    public void MainWindowClose_WaitsForBulkApplyBeforeTerminalShutdownAndCleansOwnedState()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            ActualMainWindowFixture fixture = CreateActualMainWindowFixture(windowTest);
            bool applyWriteBlockerHeld = false;
            try
            {
                CustomTableView summary = (CustomTableView)fixture.Window.FindName("customTablePlaylistSummary");
                PlaylistSummaryRow row = CreatePlaylistSummaryRow(fixture.Table);
                summary.ItemsSource = new List<PlaylistSummaryRow> { row };
                summary.SelectRowsByPredicate(_ => true);
                TestUiDispatcherHost.Drain();

                void ReleaseApplyWriteBlocker()
                {
                    if (!applyWriteBlockerHeld)
                    {
                        return;
                    }
                    applyWriteBlockerHeld = false;
                    fixture.Playlist.FreeWriterLockBMSTables();
                }

                ModalObservation<PlaylistSummaryBulkEditDialog> shutdown = OpenBulkDialog(
                    fixture.Window,
                    summary,
                    row,
                    "MainWindowPlaylistWorkspaceWpfTests.bulk-shutdown",
                    (dialog, observation) =>
                    {
                        PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel draft =
                            (PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel)dialog.DataContext;
                        draft.BmtOutputOption = draft.OnOption;
                        fixture.Playlist.AcquireWriterLockBMSTables();
                        applyWriteBlockerHeld = true;
                        RaiseButtonClick(FindAutomationButton(dialog, "PlaylistSummaryApplyBmtOutput"));
                        Task applyCompletion = dialog.WaitForApplyCompletionAsync();
                        fixture.Lifetime.RequestShutdownAction = () =>
                            observation.ApplyCompletedAtTerminalRequest = applyCompletion.IsCompleted;
                        observation.ApplyWasPending = !dialog.IsEnabled;

                        fixture.Window.Close();
                        observation.OwnerCloseWasCanceled = fixture.Window.IsVisible;
                        observation.ShutdownCountBeforeApplyCompletion = fixture.Lifetime.RequestShutdownCount;
                        void ReleaseApplyAfterOwnerShutdownClose(object sender, EventArgs args)
                        {
                            dialog.Closed -= ReleaseApplyAfterOwnerShutdownClose;
                            Assert.IsTrue(
                                dialog.IsOwnerShutdownClose,
                                "Bulk dialog must close through the owner-shutdown path before the apply lock is released.");
                            observation.ShutdownCountBeforeLockRelease = fixture.Lifetime.RequestShutdownCount;
                            ReleaseApplyWriteBlocker();
                        }
                        dialog.Closed += ReleaseApplyAfterOwnerShutdownClose;
                    });

                fixture.ModalPreparation.AssertLatest(shutdown.Window, expectedCount: 1);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    fixture.Lifetime.ShutdownRequested.Task,
                    "MainWindowPlaylistWorkspaceWpfTests.bulk-shutdown.terminal-request");
                Assert.AreEqual(
                    1,
                    fixture.Lifetime.RequestShutdownCount,
                    $"shutdown={fixture.Lifetime.RequestShutdownCount}, applyPending={shutdown.ApplyWasPending}, ownerCloseCanceled={shutdown.OwnerCloseWasCanceled}, beforeApply={shutdown.ShutdownCountBeforeApplyCompletion}, beforeRelease={shutdown.ShutdownCountBeforeLockRelease}, visible={fixture.Window.IsVisible}");
                Assert.IsNull(fixture.ViewModel.PlaylistWorkspace.ActiveSummaryBulkEditDialog);
                Assert.IsNull(shutdown.Window.DataContext);
                Assert.AreEqual(1, shutdown.DataContextDetachCount);
                Assert.IsTrue(shutdown.ApplyWasPending);
                Assert.IsTrue(shutdown.OwnerCloseWasCanceled);
                Assert.AreEqual(0, shutdown.ShutdownCountBeforeApplyCompletion);
                Assert.AreEqual(0, shutdown.ShutdownCountBeforeLockRelease);
                Assert.IsTrue(shutdown.ApplyCompletedAtTerminalRequest);
                fixture.Window.Close();
                Assert.IsFalse(fixture.Window.IsVisible);

                // Repeated owner shutdown signals remain idempotent after terminal authorization.
                fixture.ViewModel.ShellShutdownWorkflow.RequestTerminalApplicationShutdown();
                Assert.AreEqual(1, fixture.Lifetime.RequestShutdownCount);
            }
            finally
            {
                if (applyWriteBlockerHeld)
                {
                    applyWriteBlockerHeld = false;
                    fixture.Playlist.FreeWriterLockBMSTables();
                }
                fixture.Close();
            }
        });
    }

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

                    CustomTableHitTestResult hit = table.HitTestTable(new Point(20d, 30d));
                    Assert.AreEqual(CustomTableHitKind.Cell, hit.Kind);
                    Assert.AreEqual(0, hit.RowIndex);
                    Assert.AreSame(firstRow, hit.Row);
                    Assert.AreEqual("Url1", hit.Column?.Id);
                    Assert.AreEqual(CustomTableCellKind.DownloadIcon, hit.Column?.CellKind);

                    // Physical cursor input is forbidden in this lane, and CustomTableView has no
                    // deterministic typed action-dispatch seam. Keep this test's reflection narrowly
                    // scoped to the existing CellActionRequested backing subscriber until such a seam
                    // is introduced, then retire this invocation for that typed/internal route.
                    FieldInfo actionField = typeof(CustomTableView).GetField(
                        nameof(CustomTableView.CellActionRequested),
                        BindingFlags.Instance | BindingFlags.NonPublic)!;
                    Delegate? actionSubscriber = actionField.GetValue(table) as Delegate;
                    Assert.IsNotNull(actionSubscriber);
                    Assert.AreEqual(1, actionSubscriber!.GetInvocationList().Length);
                    actionSubscriber.DynamicInvoke(table, new CustomTableCellActionRequestedEventArgs(hit));
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
    public void PlaylistTableContextMenu_CompiledTreePreservesCurrentActionsForEligibleAndIneligibleTables()
    {
        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            new Settings(),
            (_, window) =>
            {
                ContextMenu menu = (ContextMenu)window.FindResource("treeViewPlaylistTableContextMenu");
                foreach (BMSTable table in new[]
                {
                    new BMSTable { Page_url = new Uri("https://example.test/external"), is_external_sync = true },
                    new BMSTable { Page_url = new Uri("https://example.test/local"), is_external_sync = false }
                })
                {
                    menu.PlacementTarget = new TreeViewItem { DataContext = table };
                    menu.DataContext = table;
                    OpenContextMenu(menu);

                    Assert.IsTrue(FindMenuItem(menu, "treeViewPlaylistTableContextMenuItemReload").IsEnabled);
                    Assert.IsTrue(FindMenuItem(menu, "treeViewPlaylistTableContextMenuItemOpenPageURI").IsEnabled);
                    Assert.IsTrue(FindMenuItem(menu, "treeViewPlaylistTableContextMenuItemOverwriteLevel").IsEnabled);
                    Assert.AreEqual(
                        !table.is_external_sync,
                        FindMenuItem(menu, "treeViewPlaylistTableContextMenuItemCreateNewFolder").IsEnabled);
                    Assert.IsTrue(FindMenuItem(menu, "treeViewPlaylistTableContextMenuItemRemoveTable").IsEnabled);
                }
            });
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

    private static void ReassertNonActivatingPosition(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        // MainWindow restores its persisted native placement during source initialization,
        // after the shared presentation scope has prepared the window. Reassert the one-shot
        // offscreen position after Loaded so that the common policy check observes the actual
        // MainWindow route without allowing foreground interaction. The extra span tolerates
        // WPF coordinates being logical pixels while the native restore uses physical pixels.
        window.Left = SystemParameters.VirtualScreenLeft
            + (SystemParameters.VirtualScreenWidth * 4d)
            + 4096d;
        window.Top = SystemParameters.VirtualScreenTop
            + (SystemParameters.VirtualScreenHeight * 4d)
            + 4096d;
    }

    private static UiDialogCoordinator CreateActualRouteDialogService(
        TestWindowPresentationScope windowTest,
        ModalPreparationRecorder modalPreparation)
    {
        ArgumentNullException.ThrowIfNull(windowTest);
        ArgumentNullException.ThrowIfNull(modalPreparation);
        return new UiDialogCoordinator(
            new UiDialogOwnerResolver(),
            dialogWindow =>
            {
                // UiDialogCoordinator invokes this modal scope after creating and owning the
                // child, immediately before ShowDialog. Keep the real coordinator route while
                // giving the shared scope its required pre-show non-activating seam.
                windowTest.PrepareForOwnedPresentation(
                    dialogWindow,
                    TestWindowActivation.NonActivating);
                modalPreparation.Record(dialogWindow);
                return UiDialogOwnerResolver.PushActiveModal(dialogWindow);
            });
    }

    private static ActualMainWindowFixture CreateActualMainWindowFixture(
        TestWindowPresentationScope windowTest)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            nameof(MainWindowPlaylistWorkspaceWpfTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        StartupLibraryConstructionTestSupport.CreateSongDatabase(songDbPath);

        var settings = new Settings
        {
            OperationModeLR2DB = false,
            BMSRootPath = root,
            StandaloneBmsRootPaths = root,
            BMSInstallDir = root,
            TableListURL = new Uri("http://127.0.0.1:1/table-list.json"),
            EnablePlaylistUrlCompletion = false,
            ScanBmsFilesOnStartup = false,
            SkipInitPlaylistLoad = true,
            UseBeatorajaScoreDb = false,
            EnableBeatorajaBmtOutput = false,
            UseExternalPanelImage = false,
            UsePlayeruBMplay = false,
            UsePlayerLR2body = false,
            UsePlayerBMIIDXView = false,
            IsLR2BackupEnabled = false
        };
        var lifetime = new RecordingApplicationLifetime();
        var modalPreparation = new ModalPreparationRecorder();
        UiDialogCoordinator actualRouteDialogService = CreateActualRouteDialogService(
            windowTest,
            modalPreparation);
        var composition = new ApplicationComposition(
            settingsEditSession: new NoOpSettingsEditSession(settings),
            uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            applicationLifetime: lifetime,
            cultureCatalog: TestApplicationContext.CreateCultureCatalog());
        var library = new TestBmsLibrary(
            songDbPath,
            getLR2Config: null,
            _lr2ScoreDB: null,
            startupRequiredFileScanReason: null,
            optionsSnapshotProvider: () => BmsLibraryOptionsSnapshot.CreateCurrent(settings));
        var playlist = MainWindowViewModelTestFactory.CreatePlaylist(songDbPath, settings);
        var table = new BMSTable
        {
            playlist_id = 1,
            bmt_sort = 1,
            name = "Native modal fixture",
            entry_type = LR2SongDBExtended.playlist.EntryUnitType.File,
            is_bmt_output = false,
            ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.None
        };
        playlist.BMSTables = new ObservableCollection<BMSTable> { table };
        var viewModel = new MainWindowViewModel(
            composition,
            new FixedStartupLibraryFactory(library, playlist));
        viewModel.StartupUpdateWorkflow.NotifyClosing();
        viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(false);
        viewModel.PlaylistWorkspace.SetPlaylistSummaryMode(true);

        bool hadPreviousViewModelResource = Application.Current.Resources.Contains("vm");
        object previousViewModelResource = hadPreviousViewModelResource
            ? Application.Current.Resources["vm"]
            : null;
        Application.Current.Resources["vm"] = viewModel;
        MainWindow window = null;
        try
        {
            Task initialization = viewModel.ShellActivationWorkflow.ActivateRenderedShell(
                () => { },
                action => action(),
                () => false);
            TestUiDispatcherHost.AwaitTaskOnDispatcher(
                initialization,
                "MainWindowPlaylistWorkspaceWpfTests.main-window-initialization");
            Assert.IsTrue(viewModel.IsInitializationCompleted);

            window = new MainWindow(
                viewModel,
                settingsWindowCreated: null,
                playlistWorkspaceDialogService: actualRouteDialogService);
            RoutedEventHandler ensureNonActivatingPosition = (_, _) =>
                window.Dispatcher.BeginInvoke(
                    DispatcherPriority.Render,
                    new Action(() => ReassertNonActivatingPosition(window)));
            window.Loaded += ensureNonActivatingPosition;
            try
            {
                windowTest.ShowAndWaitForContentRendered(window);
            }
            finally
            {
                window.Loaded -= ensureNonActivatingPosition;
            }
            return new ActualMainWindowFixture(
                root,
                viewModel,
                window,
                playlist,
                table,
                lifetime,
                modalPreparation,
                hadPreviousViewModelResource,
                previousViewModelResource);
        }
        catch
        {
            try
            {
                Task closeRequest = viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    closeRequest,
                    "MainWindowPlaylistWorkspaceWpfTests.main-window-failed-close");
                if (window?.IsVisible == true)
                {
                    window.Close();
                }
            }
            catch
            {
            }
            if (hadPreviousViewModelResource)
            {
                Application.Current.Resources["vm"] = previousViewModelResource;
            }
            else
            {
                Application.Current.Resources.Remove("vm");
            }
            viewModel.SettingDialog.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
            throw;
        }
    }

    private static ModalObservation<PlaylistPropertyDialog> OpenPropertyDialog(
        MainWindow owner,
        CustomTableView summary,
        PlaylistSummaryRow row,
        string operationName,
        Action<PlaylistPropertyDialog, ModalObservation<PlaylistPropertyDialog>> drive = null)
    {
        drive ??= (dialog, _) =>
        {
            RaiseButtonClick(FindAutomationButton(dialog, "PlaylistPropertyCancel"));
            RaiseButtonClick(FindAutomationButton(dialog, "PlaylistPropertyCancel"));
        };
        return OpenDialog(
            owner,
            summary,
            row,
            commandResourcePath: "Resources.Property",
            operationName,
            drive);
    }

    private static ModalObservation<PlaylistSummaryBulkEditDialog> OpenBulkDialog(
        MainWindow owner,
        CustomTableView summary,
        PlaylistSummaryRow row,
        string operationName,
        Action<PlaylistSummaryBulkEditDialog, ModalObservation<PlaylistSummaryBulkEditDialog>> drive = null)
    {
        drive ??= (dialog, observation) =>
        {
            PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel viewModel =
                (PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel)dialog.DataContext;
            viewModel.BmtOutputOption = viewModel.OnOption;
            RaiseButtonClick(FindAutomationButton(dialog, "PlaylistSummaryApplyBmtOutput"));
            dialog.WaitForApplyCompletionAsync().ContinueWith(
                _ => observation.CallbackGate.Queue(
                    DispatcherPriority.ApplicationIdle,
                    () =>
                    {
                        observation.VisibleAfterApply = dialog.IsVisible;
                        RaiseButtonClick(FindAutomationButton(dialog, "PlaylistSummaryClose"));
                    }),
                TaskScheduler.Default);
        };
        return OpenDialog(
            owner,
            summary,
            row,
            commandResourcePath: "Resources.Playlist_summary_bulk_edit",
            operationName,
            drive);
    }

    private static ModalObservation<TWindow> OpenDialog<TWindow>(
        MainWindow owner,
        CustomTableView summary,
        PlaylistSummaryRow row,
        string commandResourcePath,
        string operationName,
        Action<TWindow, ModalObservation<TWindow>> drive)
        where TWindow : Window
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandResourcePath);
        var opened = new TaskCompletionSource<ModalObservation<TWindow>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ModalObservation<TWindow> observation = null;
        var callbackGate = new DispatcherCallbackGate(owner.Dispatcher);

        void ObserveDialog()
        {
            if (!callbackGate.IsOpen)
            {
                return;
            }
            TWindow dialog = Application.Current.Windows
                .OfType<TWindow>()
                .FirstOrDefault(candidate => candidate.IsVisible);
            if (dialog == null)
            {
                callbackGate.Queue(
                    DispatcherPriority.ApplicationIdle,
                    ObserveDialog);
                return;
            }

            observation = new ModalObservation<TWindow>(
                dialog,
                dialog.Owner,
                !owner.IsEnabled,
                dialog.DataContext,
                callbackGate);
            dialog.DataContextChanged += observation.HandleDataContextChanged;
            drive(dialog, observation);
            opened.TrySetResult(observation);
        }

        try
        {
            ContextMenu menu = (ContextMenu)owner.FindResource("playlistSummaryContextMenu");
            menu.PlacementTarget = summary;
            menu.Tag = new CustomTableContextMenuContext(row, 0);
            MenuItem command = FindMenuItemByHeaderBindingPath(menu, commandResourcePath);
            callbackGate.Queue(
                DispatcherPriority.ApplicationIdle,
                ObserveDialog);
            RaiseMenuClick(command);

            TestUiDispatcherHost.AwaitTaskOnDispatcher(opened.Task, operationName + ".opened");
            observation = opened.Task.GetAwaiter().GetResult();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(
                observation.DataContextDetached.Task,
                operationName + ".cleanup");
            observation.Window.DataContextChanged -= observation.HandleDataContextChanged;
            return observation;
        }
        finally
        {
            callbackGate.Close();
        }
    }

    private static Button FindAutomationButton(DependencyObject root, string automationId)
    {
        Button button = FindDescendants<Button>(root)
            .FirstOrDefault(candidate => string.Equals(
                AutomationProperties.GetAutomationId(candidate),
                automationId,
                StringComparison.Ordinal));
        return button
            ?? throw new AssertFailedException($"Button '{automationId}' was not materialized.");
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

    private enum PropertyNavigationCategory
    {
        General,
        Folder,
        CustomFolder,
    }

    private sealed record PropertyNavigationItem(
        AutomationPeer Peer,
        ISelectionItemProvider Provider);

    private sealed record PropertyNavigationObservation(
        ISelectionProvider SelectionProvider,
        IReadOnlyDictionary<PropertyNavigationCategory, PropertyNavigationItem> ItemsByCategory,
        PropertyNavigationItem InitiallySelectedItem);

    private static MenuItem FindMenuItemByHeaderBindingPath(
        ItemsControl root,
        string expectedBindingPath)
    {
        MenuItem command = root.Items
            .OfType<MenuItem>()
            .SingleOrDefault(item => BindingOperations.GetBindingBase(
                    item,
                    HeaderedItemsControl.HeaderProperty) is Binding binding
                && string.Equals(
                    binding.Path?.Path,
                    expectedBindingPath,
                    StringComparison.Ordinal));
        return command
            ?? throw new AssertFailedException(
                $"Menu item with header binding '{expectedBindingPath}' was not materialized.");
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
            if (current is T typed)
            {
                yield return typed;
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

    private static void RaiseButtonClick(Button button)
        => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

    private static void RaiseKey(UIElement target, Key key)
    {
        PresentationSource source = PresentationSource.FromVisual(target);
        Assert.IsNotNull(source);
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = Keyboard.KeyDownEvent
        });
    }

    private static PlaylistSummaryRow CreatePlaylistSummaryRow(BMSTable table)
    {
        return new PlaylistSummaryRow
        {
            PlaylistId = table.playlist_id,
            Name = table.name,
            BmtSort = table.bmt_sort ?? int.MaxValue,
            IsBmtOutput = table.is_bmt_output != false,
            TableRef = table
        };
    }

    private sealed class ModalObservation<TWindow>
        where TWindow : Window
    {
        internal ModalObservation(
            TWindow window,
            Window owner,
            bool ownerEnabled,
            object dataContext,
            DispatcherCallbackGate callbackGate)
        {
            Window = window;
            Owner = owner;
            OwnerEnabled = ownerEnabled;
            DataContext = dataContext;
            CallbackGate = callbackGate;
        }

        internal TWindow Window { get; }

        internal Window Owner { get; }

        internal bool OwnerEnabled { get; }

        internal object DataContext { get; }

        internal DispatcherCallbackGate CallbackGate { get; }

        internal bool VisibleAfterApply { get; set; }

        internal bool ApplyWasPending { get; set; }

        internal bool OwnerCloseWasCanceled { get; set; }

        internal bool ApplyCompletedAtTerminalRequest { get; set; }

        internal int ShutdownCountBeforeApplyCompletion { get; set; }

        internal int ShutdownCountBeforeLockRelease { get; set; }

        internal int DataContextDetachCount { get; private set; }

        internal TaskCompletionSource<object?> DataContextDetached { get; } = NewCompletion();

        internal void HandleDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue != null)
            {
                return;
            }
            DataContextDetachCount++;
            DataContextDetached.TrySetResult(null);
        }
    }

    /// <summary>
    /// Cancels dispatcher callbacks owned by one modal observation and aborts callbacks
    /// that have not started, so a watchdog or terminal cleanup cannot keep polling alive.
    /// </summary>
    private sealed class DispatcherCallbackGate
    {
        private readonly Dispatcher dispatcher;
        private readonly object sync = new();
        private readonly HashSet<DispatcherOperation> pendingOperations = [];
        private bool isOpen = true;

        internal DispatcherCallbackGate(Dispatcher dispatcher)
        {
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        internal bool IsOpen
        {
            get
            {
                lock (sync)
                {
                    return isOpen;
                }
            }
        }

        internal void Queue(DispatcherPriority priority, Action callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            DispatcherOperation operation = null!;
            lock (sync)
            {
                if (!isOpen)
                {
                    return;
                }

                operation = dispatcher.BeginInvoke(
                    priority,
                    new Action(() => Execute(operation, callback)));
                pendingOperations.Add(operation);
            }
        }

        internal void Close()
        {
            DispatcherOperation[] operations;
            lock (sync)
            {
                if (!isOpen)
                {
                    return;
                }

                isOpen = false;
                operations = pendingOperations.ToArray();
                pendingOperations.Clear();
            }

            foreach (DispatcherOperation operation in operations)
            {
                operation.Abort();
            }
        }

        private void Execute(DispatcherOperation operation, Action callback)
        {
            lock (sync)
            {
                pendingOperations.Remove(operation);
                if (!isOpen)
                {
                    return;
                }
            }

            callback();
        }
    }

    private sealed class ModalPreparationRecorder
    {
        private readonly List<Window> preparedWindows = [];

        internal int Count => preparedWindows.Count;

        internal void Record(Window window)
        {
            ArgumentNullException.ThrowIfNull(window);
            preparedWindows.Add(window);
        }

        internal void AssertLatest(Window expectedWindow, int expectedCount)
        {
            ArgumentNullException.ThrowIfNull(expectedWindow);
            Assert.AreEqual(expectedCount, Count);
            Assert.AreSame(expectedWindow, preparedWindows[^1]);
        }
    }

    private sealed class ActualMainWindowFixture
    {
        internal ActualMainWindowFixture(
            string root,
            MainWindowViewModel viewModel,
            MainWindow window,
            TestBmsPlaylist playlist,
            BMSTable table,
            RecordingApplicationLifetime lifetime,
            ModalPreparationRecorder modalPreparation,
            bool hadPreviousViewModelResource,
            object previousViewModelResource)
        {
            Root = root;
            ViewModel = viewModel;
            Window = window;
            Playlist = playlist;
            Table = table;
            Lifetime = lifetime;
            ModalPreparation = modalPreparation;
            this.hadPreviousViewModelResource = hadPreviousViewModelResource;
            this.previousViewModelResource = previousViewModelResource;
        }

        internal string Root { get; }

        internal MainWindowViewModel ViewModel { get; }

        internal MainWindow Window { get; }

        internal TestBmsPlaylist Playlist { get; }

        internal BMSTable Table { get; }

        internal RecordingApplicationLifetime Lifetime { get; }

        internal ModalPreparationRecorder ModalPreparation { get; }

        private readonly bool hadPreviousViewModelResource;

        private readonly object previousViewModelResource;

        internal void Close()
        {
            if (Window.IsVisible)
            {
                Task closeRequest = ViewModel.ShellShutdownWorkflow.RequestWindowCloseAsync();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    closeRequest,
                    "MainWindowPlaylistWorkspaceWpfTests.fixture-close");
                Window.Close();
            }
            ViewModel.SettingDialog.Dispose();
            if (hadPreviousViewModelResource)
            {
                Application.Current.Resources["vm"] = previousViewModelResource;
            }
            else
            {
                Application.Current.Resources.Remove("vm");
            }
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class FixedStartupLibraryFactory : IStartupLibraryFactory
    {
        internal FixedStartupLibraryFactory(TestBmsLibrary library, TestBmsPlaylist playlist)
        {
            Library = library;
            Playlist = playlist;
        }

        private TestBmsLibrary Library { get; }

        private TestBmsPlaylist Playlist { get; }

        public BMSLibrary CreateBmsLibrary(LibraryProfile libraryProfile) => Library;

        public BMSPlaylist CreateBmsPlaylist(LibraryProfile libraryProfile, BMSLibrary library) => Playlist;
    }

    private sealed class RecordingApplicationLifetime : IApplicationLifetimePort
    {
        internal TaskCompletionSource<object?> ShutdownRequested { get; } = NewCompletion();

        internal int RequestShutdownCount { get; private set; }

        internal Action RequestShutdownAction { get; set; }

        public bool IsFirstStartup => false;

        public void CompleteFirstStartup()
        {
        }

        public void MarkCoordinatedShutdownStarted(string reason)
        {
        }

        public void RequestShutdown()
        {
            RequestShutdownCount++;
            ShutdownRequested.TrySetResult(null);
            RequestShutdownAction?.Invoke();
        }

        public Task RestartApplicationAsync() => Task.CompletedTask;
    }

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
