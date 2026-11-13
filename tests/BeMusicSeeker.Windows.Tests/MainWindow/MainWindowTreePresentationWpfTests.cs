using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using NLog;
using NLog.Config;
using NLog.Targets;
using Ribbit.Logging;
using static BeMusicSeeker.Tests.TreeSelectionDisplayTestSupport;

namespace BeMusicSeeker.Tests;


/// <remarks>treeの判断・要求受理は各主体の下位ケースが担い、ここでは実MainWindowのcompiled Style・選択/クリックイベントから公開先への接続と、仮想化のスクロールで選択表示が選択項目に留まることを確認します。共有Resourcesを所有して終了まで回収するため並列実行しません。</remarks>
[TestClass]
[TestCategory("Windows.Shell")]
[DoNotParallelize]
public sealed class MainWindowTreePresentationWpfTests
{
    /// <summary>導入済み譜面の確定値を準備してもcompiledヘッダーは旧題名を保ち、公開後だけ新題名となることを確認し、排他解放前の表示通知やBindingの未更新を検出します。</summary>
    [TestMethod]
    public void InstalledPackageCompiledHeaderPublishesTitleOnlyAfterOwnerRelease()
    {
        var databaseScope = new ApplicationDatabaseScope();
        BmsLibraryStateApplierTestSupport.WithTemporarySongDb(songDbPath =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(TestSettingsFactory.CreateIsolatedSettings(), (_, window) =>
            {
                var owner = new PackageLifecycleOwner(new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath),
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
    [TestMethod]
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
            var dialogs = new StartupCompletionTestFixture.CompletionDialogs
            {
                MessageObservedAsync = request => { messages.Add(request); return Task.CompletedTask; }
            };
            TaskCompletionSource reloadCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource reinitializeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var terminal = new MainWindowLibraryReloadMenuTerminal(
                () => { calls.Add("reload"); return calls.Count == 1 ? reloadCompletion.Task : Task.CompletedTask; },
                () => { calls.Add("reinitialize"); return calls.Count == 2 ? reinitializeCompletion.Task : Task.CompletedTask; });
            MainWindowPresentationTestHarness.RunConstructorOnly(
                TestSettingsFactory.CreateIsolatedSettings(),
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
                        reloadEvent = MainWindowEventCompletion.CaptureAsyncEventCompletion(() => RaiseMenuClick(normal[0]));
                        reinitializeEvent = MainWindowEventCompletion.CaptureAsyncEventCompletion(() => RaiseMenuClick(normal[1]));
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
            TestSettingsFactory.CreateIsolatedSettings(),
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
            TestSettingsFactory.CreateIsolatedSettings(),
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
            TestSettingsFactory.CreateIsolatedSettings(),
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
            return Task.FromResult(ZeroNoteRecheckStatus.Completed);
        });

        MainWindowPresentationTestHarness.RunConstructorOnly(
            TestSettingsFactory.CreateIsolatedSettings(),
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
        MainWindowPresentationTestHarness.RunConstructorOnly(TestSettingsFactory.CreateIsolatedSettings(), (_, window) =>
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

    /// <summary>
    /// 設定画面で「起動時にインストール保留を選択」を両方向へ変えても、別の項目を選んだ状態のツリー選択・一覧要求・ViewModelの表示モードが変わらないことを確認します。
    /// この設定は次回起動の初期選択にだけ使うため、変更時点の選択を動かす誤り（Bindingの常時反映）を検出します。
    /// </summary>
    [TestMethod]
    [DataRow(false, DisplayName = "偽から真へ")]
    [DataRow(true, DisplayName = "真から偽へ")]
    public void StartupSelectInstallPendingChange_DoesNotMoveCurrentSelection(bool initial)
    {
        Settings settings = TestSettingsFactory.CreateIsolatedSettings(values => values.StartupSelectInstallPending = initial);
        var calls = new List<string>();
        var folderTerminal = new MainWindowRegularLibraryTreeTerminal((kind, key) => { calls.Add("folder:" + kind + ":" + key); return true; });
        var installTerminal = new MainWindowInstallTreeTerminal((mode, _) => { calls.Add("install:" + mode); return Task.FromResult(true); });
        MainWindowPresentationTestHarness.RunConstructorOnly(settings, (scope, viewModel, window) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            var tree = (TreeView)window.FindName("treeView");
            TreeViewItem root = GetNamedElement<TreeViewItem>(window, "treeViewItemLibraryRoot");
            TreeViewItem pending = GetNamedElement<TreeViewItem>(window, "treeViewItemInstallPending");
            TreeViewItem other = GetNamedElement<TreeViewItem>(window, "treeViewItemFullScanCheck");
            Assert.IsFalse(root.IsSelected, "構築だけでは初期選択を行わない。");
            Assert.IsFalse(pending.IsSelected, "構築だけでは初期選択を行わない。");
            MainViewUpdateMode? modeBefore = viewModel.MainChartList.LastAppliedColumnMode;
            calls.Clear();
            other.IsSelected = true;
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.AreSame(other, tree.SelectedItem);
            int callsBefore = calls.Count;

            settings.StartupSelectInstallPending = !initial;
            TestUiDispatcherHost.ProcessQueuedPresentation();
            settings.StartupSelectInstallPending = initial;
            TestUiDispatcherHost.ProcessQueuedPresentation();

            Assert.AreSame(other, tree.SelectedItem, "設定を変えても現在の選択は変わらない。");
            Assert.IsFalse(root.IsSelected);
            Assert.IsFalse(pending.IsSelected);
            Assert.AreEqual(callsBefore, calls.Count, "設定の変更で一覧の要求を増やさない。");
            Assert.AreEqual(modeBefore, viewModel.MainChartList.LastAppliedColumnMode, "ViewModelの表示モードは変わらない。");
        }, allowStartupUiInteraction: true, regularLibraryTreeTerminal: folderTerminal, installTreeTerminal: installTerminal,
            maintenanceTreeTerminal: new MainWindowMaintenanceTreeTerminal((mode, _) => Task.FromResult(true)));
    }

    /// <summary>
    /// 起動中の抑止では、ライブラリのフォルダ・保守ノード・保留パッケージ・プレイリスト側の項目への左・右クリックとキーの選択移動を処理済みにして、
    /// 選択も一覧の要求も起こさず、表示と一覧の対象を食い違わせないことを確認します。抑止の解除後は同じ入力が処理済みにならず、通常どおり選べます。
    /// 抑止中に選択だけが先に変わって一覧が前の表示のまま残る誤りと、解除後も入力を止め続ける誤りを検出します。
    /// </summary>
    /// <remarks>実際のマウスとキーボードからの選択は実機の入力経路であり、ここでは同じ入力のイベントを項目へ発生させて処理済みと選択の有無を確認します。</remarks>
    [TestMethod]
    public void StartupBlockedTreeInputs_KeepSelectionAndListTarget()
    {
        var calls = new List<string>();
        var folderTerminal = new MainWindowRegularLibraryTreeTerminal((kind, key) => { calls.Add("folder:" + kind + ":" + key); return true; });
        var maintenanceTerminal = new MainWindowMaintenanceTreeTerminal((mode, _) => { calls.Add("maintenance:" + mode); return Task.FromResult(true); });
        var installTerminal = new MainWindowInstallTreeTerminal((mode, _) => { calls.Add("install:" + mode); return Task.FromResult(true); });
        MainWindowPresentationTestHarness.RunConstructorOnly(TestSettingsFactory.CreateIsolatedSettings(), (scope, viewModel, window) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            var tree = (TreeView)window.FindName("treeView");
            var playlistTree = (TreeView)window.FindName("treeViewPlaylist");
            viewModel.LibraryFolderTree.BMSParentFolderList.Add(@"C:\Songs\A");
            tree.UpdateLayout();
            TestUiDispatcherHost.ProcessQueuedPresentation();
            var folders = (TreeViewItem)FindElementWithBinding(tree, ItemsControl.ItemsSourceProperty, "LibraryFolderTree.BMSParentFolderList")!;
            TreeViewItem folder = RealizeChild(tree, folders, 0);
            TreeViewItem maintenance = GetNamedElement<TreeViewItem>(window, "treeViewItemFullScanCheck");
            TreeViewItem pending = GetNamedElement<TreeViewItem>(window, "treeViewItemInstallPending");
            var package = new TreeViewItem { DataContext = new ChartPackage { path = @"C:\blocked-package" }, Header = "package" };
            pending.Items.Add(package);
            TreeViewItem playlistRoot = GetNamedElement<TreeViewItem>(window, "treeViewItemPlaylist");
            TreeViewItem[] items = [folder, maintenance, package, playlistRoot];
            calls.Clear();
            Assert.IsTrue(viewModel.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked, "起動中の抑止を前提にする。");

            foreach (TreeViewItem item in items)
            {
                MouseButtonEventArgs left = RaiseMouse(item, Mouse.PreviewMouseDownEvent, MouseButton.Left);
                Assert.IsTrue(left.Handled, "左クリックは処理済みにする: " + item.DataContext);
                MouseButtonEventArgs right = RaiseMouse(item, Mouse.MouseDownEvent, MouseButton.Right);
                Assert.IsTrue(right.Handled, "右クリックは処理済みにする: " + item.DataContext);
                foreach (Key key in new[] { Key.Up, Key.Down, Key.Home, Key.End, Key.PageUp, Key.PageDown })
                {
                    Assert.IsTrue(RaiseKey(item, key).Handled, "選択移動キーは処理済みにする: " + key);
                }
                Assert.IsFalse(item.IsSelected, "選択を起こさない。");
            }
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.IsNull(tree.SelectedItem);
            Assert.IsNull(playlistTree.SelectedItem);
            CollectionAssert.AreEqual(Array.Empty<string>(), calls, "一覧の要求を起こさない。");

            viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(false);
            foreach (TreeViewItem item in items)
            {
                Assert.IsFalse(RaiseMouse(item, Mouse.PreviewMouseDownEvent, MouseButton.Left).Handled, "解除後の左クリックは処理済みにしない。");
                Assert.IsFalse(RaiseMouse(item, Mouse.MouseDownEvent, MouseButton.Right).Handled, "解除後の右クリックは処理済みにしない。");
                Assert.IsFalse(RaiseKey(item, Key.Down).Handled, "解除後の選択移動キーは処理済みにしない。");
            }
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.IsTrue(folder.IsSelected, "解除後は右クリックで通常どおり選べる。");
            Assert.IsTrue(calls.Contains("folder:Directory:" + @"C:\Songs\A"), "選択した項目の一覧要求が届く。");
            Assert.AreSame(folder.DataContext, tree.SelectedItem);
        }, regularLibraryTreeTerminal: folderTerminal, maintenanceTreeTerminal: maintenanceTerminal, installTreeTerminal: installTerminal);
    }

    /// <summary>
    /// 起動中の抑止でも、展開・折りたたみのキー、展開ボタンのクリック、コードからの選択は妨げないことを確認します。
    /// 選択を伴わない操作まで止めて展開できなくなる誤りと、コードからの初期選択・復元・仮想化中の選択維持を妨げる誤りを検出します。
    /// </summary>
    [TestMethod]
    public void StartupBlockedTreeInputs_AllowExpansionAndProgrammaticSelection()
    {
        MainWindowPresentationTestHarness.RunConstructorOnly(TestSettingsFactory.CreateIsolatedSettings(), (scope, viewModel, window) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            var tree = (TreeView)window.FindName("treeView");
            TreeViewItem pending = GetNamedElement<TreeViewItem>(window, "treeViewItemInstallPending");
            var package = new TreeViewItem { DataContext = new ChartPackage { path = @"C:\expand-package" }, Header = "package" };
            pending.Items.Add(package);
            tree.UpdateLayout();
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.IsTrue(viewModel.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked, "起動中の抑止を前提にする。");

            pending.IsExpanded = true;
            foreach (Key key in new[] { Key.Add, Key.Subtract, Key.Multiply })
            {
                Assert.IsFalse(RaiseKey(pending, key).Handled, "展開キーは妨げない: " + key);
            }
            Assert.IsFalse(RaiseKey(pending, Key.Left).Handled, "展開中の左は折りたたみで、選択を移さないため妨げない。");
            Assert.IsTrue(RaiseKey(pending, Key.Right).Handled, "展開中の右は子へ選択を移すため止める。");
            pending.IsExpanded = false;
            Assert.IsFalse(RaiseKey(pending, Key.Right).Handled, "折りたたみ中の右は展開で、選択を移さないため妨げない。");
            Assert.IsTrue(RaiseKey(pending, Key.Left).Handled, "折りたたみ中の左は親へ選択を移すため止める。");
            pending.IsExpanded = true;
            tree.UpdateLayout();

            ToggleButton? expander = FindDescendants<ToggleButton>(pending).FirstOrDefault();
            Assert.IsNotNull(expander, "展開ボタン");
            Assert.IsFalse(RaiseMouse(expander, Mouse.PreviewMouseDownEvent, MouseButton.Left).Handled, "展開ボタンのクリックは妨げない。");
            ScrollBar? scrollBar = FindDescendants<ScrollBar>(tree).FirstOrDefault();
            if (scrollBar != null)
            {
                Assert.IsFalse(RaiseMouse(scrollBar, Mouse.PreviewMouseDownEvent, MouseButton.Left).Handled, "スクロールバーの操作は妨げない。");
            }

            package.IsSelected = true;
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.IsTrue(package.IsSelected, "コードからの選択は妨げない。");
            Assert.AreSame(package, tree.SelectedItem);
        });
    }

    /// <summary>
    /// 抑止の解除後に、ツリーの選択表示と一覧の表示対象が一致していることを確認します。
    /// 抑止中の入力を拒否して選択も一覧要求も変えず、解除後の次の選択でツリーの選択項目と一覧の要求対象が同じ項目になります。
    /// 抑止中だけ選択表示が先に変わり、解除後も一覧が前の表示のまま食い違う誤りを検出します。
    /// </summary>
    [TestMethod]
    public void StartupBlockRelease_TreeSelectionMatchesListTarget()
    {
        var requests = new List<string?>();
        var folderTerminal = new MainWindowRegularLibraryTreeTerminal((kind, key) => { requests.Add(key); return true; });
        MainWindowPresentationTestHarness.RunConstructorOnly(TestSettingsFactory.CreateIsolatedSettings(), (scope, viewModel, window) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            var tree = (TreeView)window.FindName("treeView");
            viewModel.LibraryFolderTree.BMSParentFolderList.Add(@"C:\Songs\A");
            viewModel.LibraryFolderTree.BMSParentFolderList.Add(@"C:\Songs\B");
            tree.UpdateLayout();
            TestUiDispatcherHost.ProcessQueuedPresentation();
            var folders = (TreeViewItem)FindElementWithBinding(tree, ItemsControl.ItemsSourceProperty, "LibraryFolderTree.BMSParentFolderList")!;
            TreeViewItem first = RealizeChild(tree, folders, 0);
            TreeViewItem second = RealizeChild(tree, folders, 1);
            requests.Clear();

            Assert.IsTrue(RaiseMouse(first, Mouse.PreviewMouseDownEvent, MouseButton.Left).Handled);
            Assert.IsTrue(RaiseMouse(first, Mouse.MouseDownEvent, MouseButton.Right).Handled);
            Assert.IsFalse(first.IsSelected);
            Assert.IsNull(tree.SelectedItem);
            Assert.AreEqual(0, requests.Count, "抑止中は表示と一覧の対象のどちらも変えない。");

            viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(false);
            Assert.IsFalse(RaiseMouse(second, Mouse.PreviewMouseDownEvent, MouseButton.Left).Handled);
            second.IsSelected = true;
            TestUiDispatcherHost.ProcessQueuedPresentation();

            Assert.AreSame(second.DataContext, tree.SelectedItem);
            Assert.AreEqual(@"C:\Songs\B", requests.Last(), "一覧の要求対象はツリーの選択項目と一致する。");
            Assert.IsFalse(first.IsSelected);
        }, regularLibraryTreeTerminal: folderTerminal);
    }

    private static MouseButtonEventArgs RaiseMouse(UIElement element, RoutedEvent routedEvent, MouseButton button)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button) { RoutedEvent = routedEvent, Source = element };
        element.RaiseEvent(args);
        return args;
    }

    private static KeyEventArgs RaiseKey(UIElement element, Key key)
    {
        // 項目が未実体化でも、表示中のWindowの入力元でキーイベントを作る。
        PresentationSource source = PresentationSource.FromVisual(element)
            ?? Application.Current.Windows.Cast<Window>().Select(window => PresentationSource.FromVisual(window)).First(candidate => candidate != null)!;
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = element };
        element.RaiseEvent(args);
        return args;
    }

    /// <summary>
    /// 保証：ツリー外にフォーカスがある状態で、ライブラリのフォルダ、インストールの新規・保留、プレイ履歴のアーカイブの年と日を選び、
    /// 仮想化で入れ物が解放・再利用される位置までスクロールして戻しても、選択表示は選択中の項目だけに残り、祖先は展開されたまま、左の選択と一覧要求も変わらない。
    /// データ項目を生成する段ごとの再利用対策の接続漏れ、祖先の入れ物の再利用、再実体化での再選択、仮想化自体の停止を検出する。
    /// </summary>
    /// <remarks>
    /// 入れ物の再利用は実WPFの仮想化でしか起きないため、コンパイル済みの実ツリーを表示してスクロールする。
    /// 選択から一覧への配送と一覧の内容は、各系統の既存の接続テストが分担する。プレイリストのツリーは <c>MainWindowPlaylistWorkspaceWpfTests</c> が分担する。
    /// </remarks>
    [TestMethod]
    public void DataTreeSelectionDisplayStaysOnSelectedItemAcrossVirtualizedScroll()
    {
        const int itemCount = 200;
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directory = Path.Combine(workspace.RootDirectory, "BeMusicSeeker_TreeSelection_" + Guid.NewGuid().ToString("N"));
        string[] roots = Enumerable.Range(0, itemCount).Select(index => Path.Combine(directory, "root" + index.ToString("D3"))).ToArray();
        foreach (string root in roots) { Directory.CreateDirectory(root); }
        Settings settings = TestBmsFactory.CreateRootSettings(roots);
        settings.StartupSelectInstallPending = false;
        int installRequests = 0;
        int folderRequests = 0;
        // 選択から一覧所有者への配送までを記録し、一覧の構築は既存の接続テストへ分担する。
        var folderTerminal = new MainWindowRegularLibraryTreeTerminal((_, _) =>
        {
            folderRequests++;
            return true;
        });
        var installTerminal = new MainWindowInstallTreeTerminal((_, _) =>
        {
            installRequests++;
            return Task.FromResult(true);
        });
        TestBmsLibrary? library = null;
        var folderRefreshWorkers = new System.Collections.Concurrent.ConcurrentBag<Task>();
        try
        {
            MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(settings, (scope, viewModel, window) =>
            {
                int periodRequests = 0;
                EventHandler<PlayHistoryViewRequestActivatedEventArgs> periodActivated = (_, _) => periodRequests++;
                viewModel.PlayHistory.PeriodRequestActivated += periodActivated;
                try
                {
                    MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
                    var tree = (TreeView)window.FindName("treeView");
                    var playlistTree = (TreeView)window.FindName("treeViewPlaylist");
                    tree.Height = 180d;
                    viewModel.LibraryFolderTree.InvalidateLibraryFolderCache();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.LibraryFolderTree.WaitForDeferredRefreshIdleAsync(), "tree selection folder list refresh");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(folderRefreshWorkers), "tree selection folder list workers");
                    Assert.AreEqual(itemCount, viewModel.LibraryFolderTree.BMSParentFolderList.Count, "仮想化を起こす量のフォルダを前提にします。");
                    ScrollViewer treeScroll = FindDescendants<ScrollViewer>(tree).First();
                    string CaptureRequests() => folderRequests + "|" + installRequests + "|" + periodRequests + "|" + (playlistTree.SelectedItem == null);

                    var folders = (TreeViewItem)FindElementWithBinding(tree, ItemsControl.ItemsSourceProperty, "LibraryFolderTree.BMSParentFolderList")!;
                    TreeViewItem installed = GetNamedElement<TreeViewItem>(window, "newlyInstalledTreeViewItem");
                    TreeViewItem pending = GetNamedElement<TreeViewItem>(window, "treeViewItemInstallPending");
                    var archive = (TreeViewItem)FindElementWithBinding(tree, ItemsControl.ItemsSourceProperty, "PlayHistory.ArchivePeriodTree")!;
                    Assert.AreEqual(itemCount, installed.Items.Count);
                    Assert.AreEqual(itemCount, pending.Items.Count);
                    Assert.AreEqual(itemCount, archive.Items.Count);

                    // 一段の系統：選択項目の隣の入れ物が解放される位置まで所有者の末尾へ移し、元の位置へ戻す。
                    VerifySingleLevel("library folder", folders, 1, SelectionRoute.Synchronous);
                    VerifySingleLevel("installed", installed, 1, SelectionRoute.AsyncHandler);
                    VerifySingleLevel("pending", pending, 1, SelectionRoute.AsyncHandler);
                    VerifySingleLevel("archive year", archive, 1, SelectionRoute.WithoutHandler);

                    // 三段の系統：最新年の1月の日を選び、日の兄弟だけが解放される中間、年の兄弟も解放される遠方、元の位置で確認する。
                    TreeViewItem year = RealizeChild(tree, archive, 0);
                    year.IsExpanded = true;
                    TreeViewItem month = RealizeChild(tree, year, year.Items.Count - 1);
                    month.IsExpanded = true;
                    Assert.AreEqual(31, month.Items.Count, "31日ある月を前提にします。");
                    TreeViewItem day = RealizeChild(tree, month, 0);
                    object dayData = month.Items[0];
                    Select(day, "archive day", SelectionRoute.WithoutHandler);
                    ScrollAndAssertSelectionUnchanged(tree, archive, dayData, CaptureRequests, () =>
                    {
                        day.BringIntoView();
                        AwaitTreeLayout(tree, () => month.ItemContainerGenerator.ContainerFromIndex(1) != null, "archive day home realizes neighbor day");
                        AssertSelectionDisplay(tree, tree, dayData, "archive day before scroll", requireRealized: true);
                        double home = treeScroll.VerticalOffset;
                        FindItemsHostPanel(month).BringIndexIntoViewPublic(month.Items.Count - 1);
                        AwaitTreeLayout(tree, () => month.ItemContainerGenerator.ContainerFromIndex(1) == null, "archive day middle releases neighbor day");
                        AssertSelectionDisplay(tree, tree, dayData, "archive day middle", requireRealized: false);
                        FindItemsHostPanel(archive).BringIndexIntoViewPublic(archive.Items.Count - 1);
                        AwaitTreeLayout(tree, () => archive.ItemContainerGenerator.ContainerFromIndex(1) == null
                            && year.ItemContainerGenerator.ContainerFromIndex(0) == null, "archive day far releases neighbor year and month");
                        AssertSelectionDisplay(tree, tree, dayData, "archive day far", requireRealized: false);
                        treeScroll.ScrollToVerticalOffset(home);
                        // 元の位置では、遠方で解放された選択月の兄弟（同じ年の6月）と、選択日の隣が再び生成される。
                        AwaitTreeLayout(tree, () => year.ItemContainerGenerator.ContainerFromIndex(0) != null
                            && month.ItemContainerGenerator.ContainerFromIndex(1) != null, "archive day home realizes neighbor month and day again");
                        AssertSelectionDisplay(tree, tree, dayData, "archive day home", requireRealized: true);
                    });

                    void VerifySingleLevel(string name, TreeViewItem owner, int index, SelectionRoute route)
                    {
                        TreeViewItem item = RealizeChild(tree, owner, index);
                        object data = owner.Items[index];
                        Select(item, name, route);
                        ScrollAndAssertSelectionUnchanged(tree, owner, data, CaptureRequests, () =>
                        {
                            item.BringIntoView();
                            AwaitTreeLayout(tree, () => owner.ItemContainerGenerator.ContainerFromIndex(index + 1) != null, name + " home realizes neighbor");
                            AssertSelectionDisplay(tree, tree, data, name + " before scroll", requireRealized: true);
                            double home = treeScroll.VerticalOffset;
                            FindItemsHostPanel(owner).BringIndexIntoViewPublic(owner.Items.Count - 1);
                            AwaitTreeLayout(tree, () => owner.ItemContainerGenerator.ContainerFromIndex(index + 1) == null, name + " far releases neighbor");
                            AssertSelectionDisplay(tree, tree, data, name + " far", requireRealized: false);
                            treeScroll.ScrollToVerticalOffset(home);
                            AwaitTreeLayout(tree, () => owner.ItemContainerGenerator.ContainerFromIndex(index + 1) != null, name + " home realizes neighbor again");
                            AssertSelectionDisplay(tree, tree, data, name + " home", requireRealized: true);
                        });
                    }

                    void Select(TreeViewItem item, string name, SelectionRoute route)
                    {
                        switch (route)
                        {
                            case SelectionRoute.AsyncHandler:
                                // 導入系の選択処理はasync voidのため、終端まで回収する。
                                TestUiDispatcherHost.AwaitTaskOnDispatcher(MainWindowEventCompletion.CaptureAsyncEventCompletion(() => item.IsSelected = true),
                                    name + " selection handler");
                                break;
                            case SelectionRoute.WithoutHandler:
                                // 期間の選択は実際の履歴読取りを始め、履歴の無いこの準備では結果の反映でアーカイブの項目が空に置き換わる。
                                // 選択の瞬間だけ起動中の操作抑止で期間の選択処理を止め、ツリーの選択と選択項目の追跡は通常どおり行う。
                                // 抑止はスクロール前に解除するため、再実体化での再選択は期間の要求の増加として検出できる。
                                viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(true);
                                try { item.IsSelected = true; }
                                finally { viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(false); }
                                break;
                            default:
                                item.IsSelected = true;
                                break;
                        }
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Keyboard.ClearFocus();
                        Assert.IsFalse(tree.IsKeyboardFocusWithin, "ツリー外にフォーカスがある状態を前提にします。");
                        Assert.IsTrue(item.IsSelected, name + "：選択の前提");
                        Assert.IsNull(playlistTree.SelectedItem, name + "：他方のツリーには選択がありません。");
                    }
                }
                finally
                {
                    // 失敗時も、フォルダ一覧の背景処理をライブラリの終了とディレクトリの削除より前に回収する。
                    try { TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(folderRefreshWorkers), "tree selection folder list workers cleanup"); }
                    finally { viewModel.PlayHistory.PeriodRequestActivated -= periodActivated; }
                }
            }, installTreeTerminal: installTerminal, regularLibraryTreeTerminal: folderTerminal, prepareViewModel: viewModel =>
            {
                ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(viewModel);
                string songDb = Path.Combine(directory, "song.db");
                BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(composition.DatabaseScope, songDb, db =>
                {
                    db.CreateTable<LR2SongDB.song>();
                    db.CreateTable<LR2SongDB.folder>();
                    db.CreateTable<LR2SongDBExtended.maintenance>();
                });
                library = new TestBmsLibrary(composition.DatabaseScope, songDb, null, null,
                    new OwnedChartCollectionTestSupport.TestFileMutationService(), dialogService: new UnexpectedBmsLibraryDialogService(),
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    optionsSnapshotProvider: () => BmsLibraryOptionsSnapshot.CreateCurrent(settings),
                    operationAdmission: composition.OperationAdmission,
                    playlistOperationAdmission: composition.PlaylistOperationAdmission, settings: settings)
                { BmsCharts = [] };
                library.ChartPackagesInstalled = new ObservableCollection<ChartPackage>(Enumerable.Range(0, itemCount)
                    .Select(index => new ChartPackage { path = Path.Combine(directory, "installed" + index.ToString("D3")) }));
                library.ChartPackagesPending = new ObservableCollection<ChartPackage>(Enumerable.Range(0, itemCount)
                    .Select(index => new ChartPackage { path = Path.Combine(directory, "pending" + index.ToString("D3")) }));
                ((IStartupLibraryApplicationPort)viewModel).AttachStartupLibrary(library);
                viewModel.InstallTree.AttachLibrary(library);
                // constructor-onlyの構成では起動時の背景処理を進めないため、フォルダ一覧の遅延更新だけを背景スレッドで実行する。
                viewModel.LibraryFolderTree.ConfigureDeferredRefreshScheduler((_, work) =>
                {
                    folderRefreshWorkers.Add(Task.Run(work));
                    return true;
                });
                viewModel.LibraryFolderTree.AttachLibrary(library);
                // 約200年の各1日と、最新年の1月の31日。変更通知を出さないため、Windowの構築前に設定してBindingの初期読取りへ載せる。
                long[] playedAt = Enumerable.Range(0, itemCount)
                    .Select(offset => new DateTimeOffset(2025 - offset, 6, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds())
                    .Concat(Enumerable.Range(1, 31).Select(dayOfMonth => new DateTimeOffset(2025, 1, dayOfMonth, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()))
                    .ToArray();
                viewModel.PlayHistory.PresentationState.SetArchivePeriodTree(PlayHistoryPeriodTreeItem.BuildArchiveTree(playedAt, TimeZoneInfo.Utc));
            });
        }
        finally
        {
            library?.RequestShutdown("tree_selection_fixture");
            if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
        }
    }

    /// <summary>選択項目を選ぶときの、本番の選択処理の通し方です。</summary>
    private enum SelectionRoute
    {
        /// <summary>同期の選択処理をそのまま通します。</summary>
        Synchronous,

        /// <summary>async voidの選択処理を終端まで回収します。</summary>
        AsyncHandler,

        /// <summary>選択の瞬間だけ選択処理を止め、ツリーの選択と追跡だけを行います。</summary>
        WithoutHandler,
    }

    /// <summary>
    /// ツリーの根から所有者までの各段を展開して公開APIで生成し、仮想化で未生成の子の入れ物を生成して返します。
    /// 固定項目の所有者自身も、上位の段の仮想化で画面外では未生成になるため、根から順に生成します。
    /// </summary>
    internal static TreeViewItem RealizeChild(TreeView tree, TreeViewItem owner, int index)
    {
        var chain = new List<TreeViewItem>();
        for (DependencyObject? current = owner; current is TreeViewItem item; current = ItemsControl.ItemsControlFromItemContainer(item))
        {
            chain.Insert(0, item);
        }
        ItemsControl parent = tree;
        foreach (TreeViewItem item in chain)
        {
            ItemsControl level = parent;
            // 固定項目は項目自身が入れ物で、データ項目は入れ物から項目を引く。
            int position = level.Items.IndexOf(item);
            if (position < 0) { position = level.ItemContainerGenerator.IndexFromContainer(item); }
            Assert.IsTrue(position >= 0, "tree chain position");
            FindItemsHostPanel(level).BringIndexIntoViewPublic(position);
            AwaitTreeLayout(tree, () => level.ItemContainerGenerator.ContainerFromIndex(position) is TreeViewItem, "realize tree chain " + position);
            item.IsExpanded = true;
            tree.UpdateLayout();
            parent = item;
        }
        FindItemsHostPanel(owner).BringIndexIntoViewPublic(index);
        AwaitTreeLayout(tree, () => owner.ItemContainerGenerator.ContainerFromIndex(index) is TreeViewItem, "realize tree child " + index);
        return (TreeViewItem)owner.ItemContainerGenerator.ContainerFromIndex(index);
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
