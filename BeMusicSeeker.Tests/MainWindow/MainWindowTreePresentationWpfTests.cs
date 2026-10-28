using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NLog;
using NLog.Config;
using NLog.Targets;
using Ribbit.Logging;

namespace BeMusicSeeker.Tests;

/// <remarks>treeの判断・要求受理は各主体の下位ケースが担い、ここでは実MainWindowのcompiled Style・選択/クリックイベントから公開先への接続を確認します。共有Resourcesを所有して終了まで回収するため並列実行しません。</remarks>
[TestClass]
[DoNotParallelize]
public sealed class MainWindowTreePresentationWpfTests
{
    /// <summary>導入済み譜面の確定値を準備してもcompiledヘッダーは旧題名を保ち、公開後だけ新題名となることを確認し、排他解放前の表示通知やBindingの未更新を検出します。</summary>
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

    /// <summary>通常フォルダとrootの再読込み入口で操作失敗を一回通知し、所有された終了取消だけを静穏に終えます。rootの末尾区切りなしとfolderの合法な区切り、両メニューの2操作配送も維持します。</summary>
    [DataTestMethod]
    [DataRow(false, false)] // 普通の失敗は一回ずつ通知し、次の要求へ過去の失敗を持ち越しません。
    [DataRow(true, false)] // 終了要求のない取消は実失敗として通知します。
    [DataRow(true, true)] // 実Window終了で所有された取消だけを通知しません。
    public void LibraryContextMenusRouteReloadAndReinitializeThroughTypedTerminal(bool cancel, bool shutdown)
    {
        Logger logger = NLogWrapper.GetLogger(typeof(MainWindowTreePresentationWpfTests));
        FieldInfo? fileLoggerProvider = typeof(NLogWrapper).GetField("_FileLogger", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(fileLoggerProvider);
        var previousFileLogger = (Func<Logger>?)fileLoggerProvider.GetValue(null);
        LoggingConfiguration? previousLogging = LogManager.Configuration;
        using var logs = new MemoryTarget { Layout = "${message}|${exception:format=tostring}" };
        try
        {
            var configuration = new LoggingConfiguration();
            configuration.AddRule(LogLevel.Warn, LogLevel.Fatal, logs);
            LogManager.Configuration = configuration;
            fileLoggerProvider.SetValue(null, (Func<Logger>)(() => logger));
            List<string> calls = [];
            List<UiMessageRequest> messages = [];
            var dialogs = new MainWindowViewModelStartupProgressTests.CompletionDialogs
            {
                MessageObservedAsync = request => { messages.Add(request); return Task.CompletedTask; }
            };
            TaskCompletionSource reloadCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource reinitializeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var terminal = new MainWindowLibraryReloadMenuTerminal(
                () => { calls.Add("reload"); return calls.Count == 1 ? reloadCompletion.Task : Task.CompletedTask; },
                () => { calls.Add("reinitialize"); return calls.Count == 2 ? reinitializeCompletion.Task : Task.CompletedTask; });
            MainWindowPresentationTestHarness.RunConstructorOnly(
                MainWindowViewModelTestFactory.CreateIsolatedSettings(),
                (viewModel, window) =>
                {
                    Task reloadEvent = Task.CompletedTask;
                    Task reinitializeEvent = Task.CompletedTask;
                    try
                    {
                        var normalMenu = (ContextMenu)window.FindResource("treeViewLibraryFolderContextMenu");
                        var rootMenu = (ContextMenu)window.FindResource("treeViewLibraryFolderRootContextMenu");
                        Assert.IsTrue(rootMenu.Items.Count > 0); Assert.IsFalse(rootMenu.Items[rootMenu.Items.Count - 1] is Separator);
                        Control[] folderItems = normalMenu.Items.OfType<Control>().ToArray();
                        Assert.IsTrue(folderItems.OfType<Separator>().Any(), "folderメニューは後続操作を区切るseparatorを保ちます。");
                        Assert.IsFalse(folderItems[0] is Separator); Assert.IsFalse(folderItems[^1] is Separator);
                        for (int i = 1; i < folderItems.Length; i++) { Assert.IsFalse(folderItems[i] is Separator && folderItems[i - 1] is Separator); }
                        MenuItem[] normal = normalMenu.Items.OfType<MenuItem>().Take(2).ToArray();
                        reloadEvent = MainWindowPresentationTestHarness.CaptureAsyncEventCompletion(() => RaiseMenuClick(normal[0]));
                        reinitializeEvent = MainWindowPresentationTestHarness.CaptureAsyncEventCompletion(() => RaiseMenuClick(normal[1]));
                        Assert.IsFalse(reloadCompletion.Task.IsCompleted);
                        Assert.IsFalse(reinitializeCompletion.Task.IsCompleted);
                        if (shutdown)
                        {
                            window.Close();
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync(), "compiled-library-reload-owned-shutdown");
                            Assert.IsTrue(viewModel.ShellShutdownWorkflow.IsShutdownRequested);
                        }
                        if (cancel) { reloadCompletion.TrySetCanceled(); reinitializeCompletion.TrySetCanceled(); }
                        else
                        {
                            reloadCompletion.TrySetException(new InvalidOperationException("library diff reload failed"));
                            reinitializeCompletion.TrySetException(new InvalidOperationException("library reinitialize failed"));
                        }
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(reloadEvent, reinitializeEvent), "compiled-library-reload-events-terminal");
                        Assert.AreEqual(shutdown ? 0 : 2, messages.Count);
                        if (!shutdown)
                        {
                            Assert.IsTrue(messages.All(request => request.MessageBoxText == Resources.Msg_error_unexpected));
                            RaiseMenuClick(rootMenu.Items.OfType<MenuItem>().Take(2).ToArray()[0]);
                            RaiseMenuClick(rootMenu.Items.OfType<MenuItem>().Take(2).ToArray()[1]);
                            CollectionAssert.AreEqual(new[] { "reload", "reinitialize", "reload", "reinitialize" }, calls);
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            Assert.AreEqual(2, messages.Count);
                        }
                    }
                    finally
                    {
                        reloadCompletion.TrySetResult();
                        reinitializeCompletion.TrySetResult();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(reloadEvent, reinitializeEvent), "compiled-library-reload-events-cleanup");
                        if (shutdown)
                        {
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(window.CloseCompletion, "compiled-library-reload-close-cleanup");
                        }
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                    }
                },
                libraryReloadMenuTerminal: terminal,
                playlistWorkspaceDialogService: dialogs);
            LogManager.Flush();
            if (!cancel)
            {
                Assert.IsTrue(logs.Logs.Any(line => line.Contains("library diff reload failed", StringComparison.Ordinal)
                    && line.Contains(nameof(InvalidOperationException), StringComparison.Ordinal)));
                Assert.IsTrue(logs.Logs.Any(line => line.Contains("library reinitialize failed", StringComparison.Ordinal)
                    && line.Contains(nameof(InvalidOperationException), StringComparison.Ordinal)));
            }
        }
        finally
        {
            fileLoggerProvider.SetValue(null, previousFileLogger);
            LogManager.Configuration = previousLogging;
        }
    }

    /// <summary>実ツリーのルート選択から、絞込みのない一覧要求が一回届き、表示Taskが正常完了することを確認します。</summary>
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
                var arrived = new TaskCompletionSource<RegularChartTreeNavigationPresentationRequestedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<RegularChartTreeNavigationPresentationRequestedEventArgs> handler = (_, value) =>
                {
                    request = value;
                    arrived.TrySetResult(value);
                };
                viewModel.RegularChartList.TreeNavigationPresentationRequested += handler;
                Exception? failure = null;
                try
                {
                    var libraryRoot = (TreeViewItem)FindElementWithBinding(
                        window,
                        HeaderedItemsControl.HeaderProperty,
                        "Resources.Library")!;

                    libraryRoot.RaiseEvent(new RoutedEventArgs(TreeViewItem.SelectedEvent, libraryRoot));

                    Assert.AreEqual(1, calls.Count);
                    Assert.IsNull(calls[0].FilterKind);
                    Assert.IsNull(calls[0].FilterKey);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(arrived.Task, "regular-tree-typed-request");
                    RegularChartTreeNavigationPresentationRequestedEventArgs delivered = arrived.Task.GetAwaiter().GetResult();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(delivered.Presentation, "regular-tree-presentation-terminal");
                    Assert.IsNotNull(request);
                    Assert.AreEqual(MainViewUpdateMode.FolderFilterSelected, request!.RefreshMode);
                    Assert.IsNull(viewModel.RegularChartList.CaptureTreeFilter(enabled: true));
                }
                catch (Exception exception) { failure = exception; throw; }
                finally
                {
                    try
                    {
                        if (arrived.Task.IsCompletedSuccessfully)
                        {
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(
                                arrived.Task.Result.Presentation, "regular-tree-presentation-cleanup");
                        }
                    }
                    catch (Exception cleanupFailure) when (failure != null)
                    { failure.Data["RegularTreePresentationCleanupFailure"] = cleanupFailure.ToString(); }
                    finally { viewModel.RegularChartList.TreeNavigationPresentationRequested -= handler; }
                    // 画面移動の外側Taskは、画面を閉じる際に一覧所有者の停止処理が回収する。
                }
            },
            allowStartupUiInteraction: true,
            regularLibraryTreeTerminal: terminal);
    }

    /// <summary>全走査・無視・重複・文字化け・未登録・零ノート・解析失敗の実tree選択を各対応modeへ届けることを確認し、隣の項目へ配送する接続誤りを検出します。</summary>
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

    /// <summary>導入済みと保留のrootおよび個別packageを選び、対応modeとpackage参照を各一回渡すことを確認し、状態区分や個別対象の取り違えを検出します。</summary>
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

    /// <summary>実零ノートメニューのクリックが再検査入口へ一回届くことを確認し、compiledメニューの処理未接続や二重配送を検出します。</summary>
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

    /// <summary>実compiledツリーメニューに旧ルート編集入口がなく、残る再読込・再初期化が既通知失敗を一回消費することを確認します。</summary>
    [TestMethod]
    public void LibraryMenuKeepsReloadRoutesAndOmitsRootEditing()
    {
        var failure = new LibraryDirectoryPreflightException(LibraryDirectoryPreflightUse.BmsRoot,
            @"C:\missing-bms-root", LibraryDirectoryPreflightFailureCause.NotFound, "missing");
        int reloadCalls = 0;
        int reinitializeCalls = 0;
        var terminal = new MainWindowLibraryReloadMenuTerminal(
            () => { reloadCalls++; return Task.FromException(failure); },
            () => { reinitializeCalls++; return Task.FromException(failure); });
        MainWindowPresentationTestHarness.RunConstructorOnly(MainWindowViewModelTestFactory.CreateIsolatedSettings(), (_, window) =>
        {
            var menu = (ContextMenu)window.FindResource("treeViewLibraryFolderContextMenu");
            MenuItem[] items = menu.Items.OfType<MenuItem>().ToArray();
            Assert.IsFalse(items.Any(item => BindingOperations.GetBinding(item, HeaderedItemsControl.HeaderProperty) is Binding binding
                && binding.Path.Path is "Resources.Cancel_root_folder" or "Resources.Add_root_folder"));
            RaiseMenuClick(items[0]); RaiseMenuClick(items[1]);
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.AreEqual(1, reloadCalls); Assert.AreEqual(1, reinitializeCalls);
        }, allowStartupUiInteraction: true, libraryReloadMenuTerminal: terminal);
    }

    /// <summary>保留を起動時に選ぶ利用者設定で構成した実MainWindowが保留rootを選択することを確認し、初期選択の設定を無視する接続漏れを検出します。</summary>
    [TestMethod]
    public void ConstructorAppliesSavedInitialPendingSelection()
    {
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.StartupSelectInstallPending = true;
            });

        MainWindowPresentationTestHarness.RunConstructorOnly(settings, (viewModel, window) =>
        {
            TreeViewItem pending = GetNamedElement<TreeViewItem>(window, "treeViewItemInstallPending");
            Assert.IsTrue(pending.IsSelected);
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
    /// <summary>compiledイベントのasync void寿命を現在のDispatcherへ接続し、Taskや通知の完了から推測せず実ハンドラー終端を返します。</summary>
    internal static Task CaptureAsyncEventCompletion(Action raiseEvent)
    {
        SynchronizationContext original = SynchronizationContext.Current
            ?? throw new InvalidOperationException("The event fixture requires the WPF synchronization context.");
        var context = new EventCompletionContext(original);
        SynchronizationContext.SetSynchronizationContext(context);
        try { raiseEvent(); }
        finally { SynchronizationContext.SetSynchronizationContext(original); }
        return context.Completion.Task;
    }

    /// <summary>async voidの開始・終端通知だけを観測し、処理・例外の配送は実Dispatcherへ委譲します。</summary>
    private sealed class EventCompletionContext(SynchronizationContext original) : SynchronizationContext
    {
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int activeOperations;
        public override void OperationStarted() { Interlocked.Increment(ref activeOperations); original.OperationStarted(); }
        public override void OperationCompleted()
        {
            original.OperationCompleted();
            if (Interlocked.Decrement(ref activeOperations) == 0) { Completion.TrySetResult(); }
        }
        public override void Post(SendOrPostCallback callback, object? state) => original.Post(callback, state);
        public override void Send(SendOrPostCallback callback, object? state) => original.Send(callback, state);
        public override SynchronizationContext CreateCopy() => this;
    }

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
        IUiDialogService? playlistWorkspaceDialogService = null)
    {
        ArgumentNullException.ThrowIfNull(test);
        RunConstructorOnly(settings, (_, viewModel, window) => test(viewModel, window),
            allowStartupUiInteraction, libraryReloadMenuTerminal, regularLibraryTreeTerminal,
            maintenanceTreeTerminal, installTreeTerminal, zeroNoteRecheckTerminal,
            columnResetTerminal, playlistWorkspaceDialogService);
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
        IUiDialogService? playlistWorkspaceDialogService = null)
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
                cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                playlistWorkspaceDialogService: playlistWorkspaceDialogService)
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
                    folderAutoRenameTerminal: null,
                    duplicateMaintenanceTerminal: null,
                    maintenanceRescanTerminal: null,
                    packageCatalogTerminal: null,
                    pendingInstallEstimationTerminal: null,
                    pendingInstallationTerminal: null,
                    installedLocationRepairTerminal: null,
                    pendingBulkMaintenanceTerminal: null,
                    mainChartCellEditTerminal: null,
                    playlistWorkspaceDialogService: playlistWorkspaceDialogService));
                TestUiDispatcherHost.ProcessQueuedPresentation();
                test(windowTest, viewModel, window);
            });
        });
    }

    /// <summary>本番起動を開始せず、元Windowの資源とcompiled内容・Bindingを同じscopeの実Windowで描画します。</summary>
    internal static void ShowCompiledContent(TestWindowPresentationScope scope, MainWindowViewModel viewModel, MainWindow window)
    {
        object content = window.Content;
        window.Content = null;
        var host = new Window { Resources = window.Resources, Content = content, DataContext = viewModel, Width = 1000, Height = 700 };
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

        public void ExitForUnrestoredDestination(System.Exception failure)
        => throw new System.InvalidOperationException("Unexpected unrestored destination exit.", failure);

        public void RequestShutdown() => ShutdownRequested.TrySetResult();

        public Task RestartApplicationAsync() => Task.CompletedTask;
    }
}
