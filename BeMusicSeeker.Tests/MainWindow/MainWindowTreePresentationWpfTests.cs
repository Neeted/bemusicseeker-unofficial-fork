using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MainWindowTreePresentationWpfTests
{
    [TestMethod]
    public void InstalledPackageCompiledHeaderPublishesTitleOnlyAfterOwnerRelease()
    {
        BmsLibraryStateApplierTestSupport.WithTemporarySongDb(songDbPath =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(MainWindowViewModelTestFactory.CreateIsolatedSettings(), (_, window) =>
            {
                var owner = new PackageLifecycleOwner(new BmsLibraryDbGateway(songDbPath),
                    new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher), _ => { },
                    packages => new System.Collections.ObjectModel.ObservableCollection<ChartPackage>(packages ?? []), () => { }, _ => { });
                ChartFile before = ChartTestValues.Empty(ChartFileKind.Bms) with
                {
                    Token = new OwnedChartToken(),
                    Title = "A header",
                    RawTitle = "A header",
                    Path = @"C:\Charts\header.bms",
                    Md5 = new string('a', 32),
                    Sha256 = new string('a', 64)
                };
                var entry = PackageChartEntry.FromChart(before);
                var package = ChartPackage.FromChartEntries([entry]);
                owner.ReplaceInstalledPackages([package]);
                TreeViewItem installed = GetNamedElement<TreeViewItem>(window, "newlyInstalledTreeViewItem");
                var item = new TreeViewItem { DataContext = package, Style = installed.ItemContainerStyle };
                try
                {
                    Assert.AreEqual("A header", item.Header);
                    Action publish = owner.PrepareCommittedChartApplication([before with { Title = "Z header", RawTitle = "Z header" }]);
                    Assert.AreEqual("A header", item.Header);
                    publish();
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreEqual("Z header", item.Header);
                    Assert.AreEqual(before.Path, entry.Chart.Path);
                }
                finally
                {
                    BindingOperations.ClearAllBindings(item);
                    item.Style = null;
                    item.DataContext = null;
                    owner.ClearInstalledPackages();
                }
            });
        });
    }

    [TestMethod]
    public void LibraryContextMenusRouteReloadAndReinitializeThroughTypedTerminal()
    {
        List<string> calls = [];
        var terminal = new MainWindowLibraryReloadMenuTerminal(
            () =>
            {
                calls.Add("reload");
                return Task.CompletedTask;
            },
            () =>
            {
                calls.Add("reinitialize");
                return Task.CompletedTask;
            });

        MainWindowPresentationTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (_, window) =>
            {
                var normalMenu = (ContextMenu)window.FindResource("treeViewLibraryFolderContextMenu");
                var rootMenu = (ContextMenu)window.FindResource("treeViewLibraryFolderRootContextMenu");

                RaiseMenuClick(normalMenu.Items.OfType<MenuItem>().Take(2).ToArray()[0]);
                RaiseMenuClick(normalMenu.Items.OfType<MenuItem>().Take(2).ToArray()[1]);
                RaiseMenuClick(rootMenu.Items.OfType<MenuItem>().Take(2).ToArray()[0]);
                RaiseMenuClick(rootMenu.Items.OfType<MenuItem>().Take(2).ToArray()[1]);

                CollectionAssert.AreEqual(
                    new[] { "reload", "reinitialize", "reload", "reinitialize" },
                    calls);
            },
            libraryReloadMenuTerminal: terminal);
    }

    [TestMethod]
    public void RegularLibrarySelectionUsesActualTreeOwnerRoute()
    {
        MainWindowViewModel? createdViewModel = null;
        List<(RegularChartFolderFilterKind? FilterKind, string FilterKey)> calls = [];
        var terminal = new MainWindowRegularLibraryTreeTerminal((filterKind, filterKey) =>
        {
            calls.Add((filterKind, filterKey));
            return createdViewModel!.RegularChartList.NavigateTree(filterKind, filterKey);
        });

        MainWindowPresentationTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (viewModel, window) =>
            {
                createdViewModel = viewModel;
                RegularChartTreeNavigationPresentationRequestedEventArgs? request = null;
                viewModel.RegularChartList.TreeNavigationPresentationRequested += (_, value) => request = value;
                var libraryRoot = (TreeViewItem)FindElementWithBinding(
                    window,
                    HeaderedItemsControl.HeaderProperty,
                    "Resources.Library")!;

                libraryRoot.RaiseEvent(new RoutedEventArgs(TreeViewItem.SelectedEvent, libraryRoot));

                Assert.AreEqual(1, calls.Count);
                Assert.IsNull(calls[0].FilterKind);
                Assert.IsNull(calls[0].FilterKey);
                Assert.IsNotNull(request);
                Assert.AreEqual(MainViewUpdateMode.FolderFilterSelected, request!.RefreshMode);
                Assert.IsNull(viewModel.RegularChartList.CaptureTreeFilter(enabled: true));
            },
            allowStartupUiInteraction: true,
            regularLibraryTreeTerminal: terminal);
    }

    [TestMethod]
    public void MaintenanceTreeSelectionsUseActualCompiledRoutes()
    {
        List<(MainViewUpdateMode Mode, object? Parameter)> calls = [];
        var terminal = new MainWindowMaintenanceTreeTerminal(
            (mode, parameter) =>
            {
                calls.Add((mode, parameter));
                return Task.FromResult(true);
            });

        MainWindowPresentationTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (_, window) =>
            {
                calls.Clear();
                (string BindingPath, MainViewUpdateMode Mode)[] routes =
                [
                    ("Resources.Full_scan_check", MainViewUpdateMode.FileMissingFilterSelected),
                    ("Resources.Full_scan_all_charts", MainViewUpdateMode.FullScanAllChartsFilterSelected),
                    ("Resources.Ignore_list", MainViewUpdateMode.FileMissingIgnoredFilterSelected),
                    ("Resources.Search_duplicates", MainViewUpdateMode.DuplicateFilterSelected),
                    ("Resources.Search_garbled", MainViewUpdateMode.GarbledFilterSelected),
                    ("Resources.Fixed", MainViewUpdateMode.GarbleFixedFilterSelected),
                    ("Resources.Unregistered_in_lr2_db", MainViewUpdateMode.UnregisteredFilterSelected),
                    ("Resources.Search_zero_note", MainViewUpdateMode.ZeroNoteFilterSelected),
                    ("Resources.Chart_info_parse_errors", MainViewUpdateMode.ChartInfoParseErrorFilterSelected)
                ];

                foreach ((string bindingPath, MainViewUpdateMode mode) in routes)
                {
                    var item = (TreeViewItem)FindElementWithBinding(
                        window,
                        HeaderedItemsControl.HeaderProperty,
                        bindingPath)!;
                    item.RaiseEvent(new RoutedEventArgs(TreeViewItem.SelectedEvent, item));
                }

                Assert.AreEqual(routes.Length, calls.Count);
                for (int i = 0; i < routes.Length; i++)
                {
                    Assert.AreEqual(routes[i].Mode, calls[i].Mode);
                    Assert.IsNull(calls[i].Parameter);
                }
            },
            allowStartupUiInteraction: true,
            maintenanceTreeTerminal: terminal);
    }

    [TestMethod]
    public void InstallTreeSelectionsUseActualCompiledRoutes()
    {
        List<(MainViewUpdateMode Mode, object? Parameter)> calls = [];
        var terminal = new MainWindowInstallTreeTerminal(
            (mode, parameter) =>
            {
                calls.Add((mode, parameter));
                return Task.FromResult(true);
            });

        MainWindowPresentationTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (_, window) =>
            {
                calls.Clear();
                TreeViewItem installed = GetNamedElement<TreeViewItem>(window, "newlyInstalledTreeViewItem");
                TreeViewItem pending = GetNamedElement<TreeViewItem>(window, "treeViewItemInstallPending");
                var installedPackage = new ChartPackage { path = @"C:\\wave6e-installed-package" };
                var pendingPackage = new ChartPackage { path = @"C:\\wave6e-pending-package" };
                var installedPackageItem = new TreeViewItem { DataContext = installedPackage };
                var pendingPackageItem = new TreeViewItem { DataContext = pendingPackage };
                installed.Items.Add(installedPackageItem);
                pending.Items.Add(pendingPackageItem);

                installed.RaiseEvent(new RoutedEventArgs(TreeViewItem.SelectedEvent, installed));
                pending.RaiseEvent(new RoutedEventArgs(TreeViewItem.SelectedEvent, pending));
                installedPackageItem.RaiseEvent(new RoutedEventArgs(TreeViewItem.SelectedEvent, installedPackageItem));
                pendingPackageItem.RaiseEvent(new RoutedEventArgs(TreeViewItem.SelectedEvent, pendingPackageItem));

                Assert.AreEqual(4, calls.Count);
                Assert.AreEqual(MainViewUpdateMode.NewlyInstalledFolderSelected, calls[0].Mode);
                Assert.AreEqual(MainViewUpdateMode.PendingInstallFolderSelected, calls[1].Mode);
                Assert.IsNull(calls[0].Parameter);
                Assert.IsNull(calls[1].Parameter);
                Assert.AreEqual(MainViewUpdateMode.NewlyInstalledFolderSelected, calls[2].Mode);
                Assert.AreSame(installedPackage, calls[2].Parameter);
                Assert.AreEqual(MainViewUpdateMode.PendingInstallFolderSelected, calls[3].Mode);
                Assert.AreSame(pendingPackage, calls[3].Parameter);
            },
            allowStartupUiInteraction: true,
            installTreeTerminal: terminal);
    }

    [TestMethod]
    public void ZeroNoteRecheckUsesActualCompiledMenuRoute()
    {
        int calls = 0;
        var terminal = new MainWindowZeroNoteRecheckTerminal(() =>
        {
            calls++;
            return Task.FromResult(true);
        });

        MainWindowPresentationTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (_, window) =>
            {
                var menu = (ContextMenu)window.FindResource("treeViewZeroNoteContextMenu");
                RaiseMenuClick(menu.Items.OfType<MenuItem>().Single());
                Assert.AreEqual(1, calls);
            },
            zeroNoteRecheckTerminal: terminal);
    }

    [TestMethod]
    public void RootFolderUnregisterUsesActualCompiledMenuRoute()
    {
        int calls = 0;
        string? requestedPath = null;
        var terminal = new MainWindowRootFolderUnregisterTerminal(path =>
        {
            calls++;
            requestedPath = path;
            return Task.CompletedTask;
        });

        MainWindowPresentationTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (_, window) =>
            {
                var menu = (ContextMenu)window.FindResource("treeViewLibraryFolderContextMenu");
                menu.PlacementTarget = new TreeViewItem { Header = @"C:\\wave6e-root" };
                MenuItem unregister = menu.Items
                    .OfType<MenuItem>()
                    .Single(item => BindingOperations.GetBinding(item, HeaderedItemsControl.HeaderProperty) is Binding binding
                        && string.Equals(binding.Path?.Path, "Resources.Cancel_root_folder", StringComparison.Ordinal));

                RaiseMenuClick(unregister);

                Assert.AreEqual(1, calls);
                Assert.AreEqual(@"C:\\wave6e-root", requestedPath);
            },
            rootFolderUnregisterTerminal: terminal);
    }

    [TestMethod]
    public void DirectoryPreflightFailureIsConsumedByLibraryShellTerminals()
    {
        var failure = new LibraryDirectoryPreflightException(
            LibraryDirectoryPreflightUse.BmsRoot,
            @"C:\missing-bms-root",
            LibraryDirectoryPreflightFailureCause.NotFound,
            "missing");
        int reloadCalls = 0;
        int reinitializeCalls = 0;
        int unregisterCalls = 0;
        var reloadTerminal = new MainWindowLibraryReloadMenuTerminal(
            () =>
            {
                reloadCalls++;
                return Task.FromException(failure);
            },
            () =>
            {
                reinitializeCalls++;
                return Task.FromException(failure);
            });
        var unregisterTerminal = new MainWindowRootFolderUnregisterTerminal(_ =>
        {
            unregisterCalls++;
            return Task.FromException(failure);
        });

        MainWindowPresentationTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (_, window) =>
            {
                var menu = (ContextMenu)window.FindResource("treeViewLibraryFolderContextMenu");
                MenuItem[] reloadCommands = menu.Items.OfType<MenuItem>().Take(2).ToArray();
                RaiseMenuClick(reloadCommands[0]);
                RaiseMenuClick(reloadCommands[1]);

                menu.PlacementTarget = new TreeViewItem { Header = @"C:\missing-bms-root" };
                MenuItem unregister = menu.Items
                    .OfType<MenuItem>()
                    .Single(item => BindingOperations.GetBinding(item, HeaderedItemsControl.HeaderProperty) is Binding binding
                        && string.Equals(binding.Path?.Path, "Resources.Cancel_root_folder", StringComparison.Ordinal));
                RaiseMenuClick(unregister);
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.AreEqual(1, reloadCalls);
                Assert.AreEqual(1, reinitializeCalls);
                Assert.AreEqual(1, unregisterCalls);
            },
            allowStartupUiInteraction: true,
            libraryReloadMenuTerminal: reloadTerminal,
            rootFolderUnregisterTerminal: unregisterTerminal);
    }

    [TestMethod]
    public void ConstructorBindsTreePresentationThroughChildOwners()
    {
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.StartupSelectInstallPending = true;
            });

        MainWindowPresentationTestHarness.RunConstructorOnly(settings, (viewModel, window) =>
        {
            TreeViewItem installed = GetNamedElement<TreeViewItem>(window, "newlyInstalledTreeViewItem");
            TreeViewItem pending = GetNamedElement<TreeViewItem>(window, "treeViewItemInstallPending");
            TreeViewItem duplicate = GetNamedElement<TreeViewItem>(window, "treeViewItemSearchDuplicated");

            AssertBindingPath(installed, ItemsControl.ItemsSourceProperty, "InstallTree.ChartPackagesInstalled");
            AssertBindingPath(pending, ItemsControl.ItemsSourceProperty, "InstallTree.ChartPackagesPending");
            AssertBindingPath(duplicate, ItemsControl.ItemsSourceProperty, "MaintenanceTree.DuplicateChartGroups");

            FrameworkElement? folder = FindElementWithBinding(
                window,
                ItemsControl.ItemsSourceProperty,
                "LibraryFolderTree.BMSParentFolderList");
            Assert.IsNotNull(folder);

            Assert.IsTrue(pending.IsSelected);
            Assert.IsTrue(viewModel.ViewSettings.StartupSelectInstallPending);
            Assert.AreSame(viewModel, ((FrameworkElement)installed).DataContext);
            Assert.AreSame(viewModel, ((FrameworkElement)duplicate).DataContext);
        });
    }

    [TestMethod]
    public void ConstructorExposesSafeLibraryContextMenuAndLr2CompatibilityNode()
    {
        MainWindowPresentationTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (_, window) =>
            {
                var contextMenu = (ContextMenu)window.FindResource("treeViewLibraryFolderContextMenu");
                MenuItem[] commands = contextMenu.Items.OfType<MenuItem>().Take(2).ToArray();

                Assert.AreEqual(2, commands.Length);
                AssertBindingPath(commands[0], HeaderedItemsControl.HeaderProperty, "Resources.Reload");
                AssertBindingPath(commands[1], HeaderedItemsControl.HeaderProperty, "Resources.Reinitialize_library");
                Assert.AreEqual(Resources.Reload, commands[0].Header);
                Assert.AreEqual(Resources.Reinitialize_library, commands[1].Header);

                FrameworkElement? unregistered = FindElementWithBinding(
                    window,
                    HeaderedItemsControl.HeaderProperty,
                    "Resources.Unregistered_in_lr2_db");
                Assert.IsNotNull(unregistered);
                Assert.AreEqual(Visibility.Visible, unregistered.Visibility);
            });
    }

    private static T GetNamedElement<T>(MainWindow window, string name)
        where T : class
    {
        object? element = window.FindName(name);
        Assert.IsInstanceOfType(element, typeof(T), name);
        return (T)element!;
    }

    private static void RaiseMenuClick(MenuItem item)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
    }

    private static void AssertBindingPath(
        DependencyObject element,
        DependencyProperty property,
        string expectedPath)
    {
        BindingBase? binding = BindingOperations.GetBindingBase(element, property);
        Assert.IsInstanceOfType(binding, typeof(Binding), expectedPath);
        Assert.AreEqual(expectedPath, ((Binding)binding!).Path?.Path);
    }

    private static FrameworkElement? FindElementWithBinding(
        DependencyObject root,
        DependencyProperty property,
        string expectedPath)
    {
        var visited = new HashSet<DependencyObject>();
        return FindElementWithBindingCore(root, property, expectedPath, visited);
    }

    private static FrameworkElement? FindElementWithBindingCore(
        DependencyObject current,
        DependencyProperty property,
        string expectedPath,
        HashSet<DependencyObject> visited)
    {
        if (!visited.Add(current))
        {
            return null;
        }

        if (current is FrameworkElement element
            && BindingOperations.GetBindingBase(element, property) is Binding binding
            && string.Equals(binding.Path?.Path, expectedPath, StringComparison.Ordinal))
        {
            return element;
        }

        foreach (object child in LogicalTreeHelper.GetChildren(current))
        {
            if (child is DependencyObject dependencyChild)
            {
                FrameworkElement? match = FindElementWithBindingCore(
                    dependencyChild,
                    property,
                    expectedPath,
                    visited);
                if (match != null)
                {
                    return match;
                }
            }
        }

        return null;
    }
}

internal static class MainWindowPresentationTestHarness
{
    /// <summary>constructor-onlyの表示保証に不要な実起動を、既存のactivation境界で抑止します。</summary>
    /// <remarks>実起動はMainWindowViewHostTestsとPlaylistのActualMainWindowFixtureが本番経路を検証します。</remarks>
    internal static void PrepareConstructorOnlyShell(MainWindowViewModel viewModel)
    {
        PropertyInfo activation = typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.ShellActivationWorkflow),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("constructor-only activation boundary is unavailable.");
        activation.SetValue(viewModel,
            new ShellActivationWorkflowOwner(viewModel.StartupUpdateWorkflow,
                new ElevatedProcessWarningWorkflowOwner(() => false), () => Task.FromResult(true)));
    }

    /// <summary>実親Windowの終端と設定復元を回収してconstructor-only操作を検証します。</summary>
    internal static void RunConstructorOnly(
        Settings settings,
        Action<MainWindowViewModel, MainWindow> test,
        bool allowStartupUiInteraction = false,
        MainWindowLibraryReloadMenuTerminal? libraryReloadMenuTerminal = null,
        MainWindowRegularLibraryTreeTerminal? regularLibraryTreeTerminal = null,
        MainWindowMaintenanceTreeTerminal? maintenanceTreeTerminal = null,
        MainWindowInstallTreeTerminal? installTreeTerminal = null,
        MainWindowZeroNoteRecheckTerminal? zeroNoteRecheckTerminal = null,
        MainWindowColumnResetTerminal? columnResetTerminal = null,
        MainWindowRootFolderUnregisterTerminal? rootFolderUnregisterTerminal = null)
    {
        ArgumentNullException.ThrowIfNull(test);
        RunConstructorOnly(settings, (_, viewModel, window) => test(viewModel, window),
            allowStartupUiInteraction, libraryReloadMenuTerminal, regularLibraryTreeTerminal,
            maintenanceTreeTerminal, installTreeTerminal, zeroNoteRecheckTerminal,
            columnResetTerminal, rootFolderUnregisterTerminal);
    }

    /// <summary>表示する実親Windowを、終了処理を所有する同じscopeで追跡します。</summary>
    internal static void RunConstructorOnly(
        Settings settings,
        Action<TestWindowPresentationScope, MainWindowViewModel, MainWindow> test,
        bool allowStartupUiInteraction = false,
        MainWindowLibraryReloadMenuTerminal? libraryReloadMenuTerminal = null,
        MainWindowRegularLibraryTreeTerminal? regularLibraryTreeTerminal = null,
        MainWindowMaintenanceTreeTerminal? maintenanceTreeTerminal = null,
        MainWindowInstallTreeTerminal? installTreeTerminal = null,
        MainWindowZeroNoteRecheckTerminal? zeroNoteRecheckTerminal = null,
        MainWindowColumnResetTerminal? columnResetTerminal = null,
        MainWindowRootFolderUnregisterTerminal? rootFolderUnregisterTerminal = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(test);

        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var lifetime = new PresentationApplicationLifetime();
            MainWindowViewModel viewModel = new ApplicationComposition(
                settingsEditSession: new NoOpSettingsEditSession(settings),
                uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                applicationLifetime: lifetime,
                cultureCatalog: TestApplicationContext.CreateCultureCatalog())
                .CreateMainWindowViewModelForTest();
            var ownership = new MainWindowTestLifetime(viewModel, lifetime.ShutdownRequested.Task);
            ownership.Run(() =>
            {
                PrepareConstructorOnlyShell(viewModel);
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(!allowStartupUiInteraction);
                MainWindow window = ownership.CreateWindow(() => new MainWindow(
                    viewModel,
                    settingsWindowCreated: null,
                    libraryReloadMenuTerminal: libraryReloadMenuTerminal,
                    regularLibraryTreeTerminal: regularLibraryTreeTerminal,
                    maintenanceTreeTerminal: maintenanceTreeTerminal,
                    installTreeTerminal: installTreeTerminal,
                    zeroNoteRecheckTerminal: zeroNoteRecheckTerminal,
                    columnResetTerminal: columnResetTerminal,
                    rootFolderUnregisterTerminal: rootFolderUnregisterTerminal));
                TestUiDispatcherHost.ProcessQueuedPresentation();
                test(windowTest, viewModel, window);
            });
        });
    }

    /// <summary>本番起動を開始せず、compiled内容とBindingを同じscopeの実Windowで描画します。</summary>
    internal static void ShowCompiledContent(TestWindowPresentationScope scope, MainWindowViewModel viewModel, MainWindow window)
    {
        object content = window.Content;
        window.Content = null;
        var host = new Window { Content = content, DataContext = viewModel, Width = 1000, Height = 700 };
        scope.ShowAndWaitForContentRendered(host);
    }

    /// <summary>実 Window の terminal 完了を観測し、共有 test Application の終了を抑止する。</summary>
    internal sealed class PresentationApplicationLifetime : IApplicationLifetimePort
    {
        internal TaskCompletionSource ShutdownRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsFirstStartup => false;

        public void CompleteFirstStartup() { }

        public void MarkCoordinatedShutdownStarted(string reason) { }

        public void RequestShutdown() => ShutdownRequested.TrySetResult();

        public Task RestartApplicationAsync() => Task.CompletedTask;
    }
}
