using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using static BeMusicSeeker.TestSupport.Components.SelectedChartAudioConversionWorkflowOwnerTestSupport;
using static BeMusicSeeker.TestSupport.Components.SplitPlacementTestSupport;

namespace BeMusicSeeker.Tests;

public sealed partial class MainWindowPackageMaintenanceWpfTests
{
    /// <summary>
    /// 保証：コンパイル済みのメンテナンスツリーで、分裂配置のルートの見出しはリソースの文言であり、ルートの選択が探索の進捗（所有者はメインウィンドウ）を経て、
    /// グループの譜面を一覧へ表示し、フォルダ項目は「エクスプローラーで開く」だけを持つ（統合先メニューも Ctrl+G による統合・整理もない）。
    /// 結果が失効した後は、受付が使用中のとき、Busy案内を一件だけ出し、進捗を表示せず、表示中の一覧のインスタンスも変えない。
    /// 検出する誤り：ルートの未接続・見出しの取り違え、進捗の所有者の欠落、重複用のメニュー・ショートカットの混入、Busy時の一覧の置換・進捗の表示。
    /// </summary>
    [TestMethod]
    public void SplitPlacementTree_RootSelectionRunsProgressUnderMainWindowAndOffersOnlyExplorerOnFolders()
    {
        var dialogs = new RecordingDialogService(new EventLog());
        int duplicateOperationCalls = 0;
        var terminal = new MainWindowDuplicateMaintenanceTerminal(
            (_, _, _) => { duplicateOperationCalls++; return Task.FromResult(DuplicateMaintenanceMutationResult.Rejected(null)); },
            (_, _) => { duplicateOperationCalls++; return Task.FromResult(DuplicateMaintenanceMutationResult.Rejected(null)); },
            () => { duplicateOperationCalls++; return true; });
        RunDuplicateSelectionFixture((scope, viewModel, window, library, charts, navigation) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            string related = charts[0].Directory, other = charts[2].Directory;
            SetIndex(library, new(related, Keys(1, 10)), new(other, Keys(1, 10)));
            var root = (TreeViewItem)window.FindName("treeViewItemSearchSplitPlacement");
            Assert.AreEqual(Resources.Search_split_placement, root.Header);
            Assert.AreEqual(0, root.Items.Count, "探索前は子を持たない。");

            Task handler = MainWindowEventCompletion.CaptureAsyncEventCompletion(() => root.IsSelected = true);
            TestUiDispatcherHost.AwaitTaskOnDispatcher(handler, "split placement root selection handler");
            TestUiDispatcherHost.ProcessQueuedPresentation();

            Assert.IsNotNull(dialogs.ProgressRequest, "探索の進捗を表示する。");
            Assert.AreSame(window, dialogs.ProgressRequest.Owner, "進捗画面の所有者はメインウィンドウ。");
            Assert.AreEqual(Resources.Split_placement_search_progress_title, dialogs.ProgressRequest.Title);
            CollectionAssert.AreEquivalent(new[] { charts[0].Path, charts[2].Path },
                viewModel.MainChartList.Rows.Cast<LibraryChartRow>().Select(row => row.Chart.Path).ToArray(), "グループの譜面だけを表示する。");
            Assert.AreEqual(1, root.Items.Count);
            DuplicateGroup group = library.SplitPlacementGroups!.Single();
            TreeViewItem folderItem = RealizeDuplicateFolderItem(root, group, related);
            var groupItem = (TreeViewItem)root.ItemContainerGenerator.ContainerFromIndex(0);
            folderItem.Style = groupItem.ItemContainerStyle;
            ContextMenu menu = folderItem.ContextMenu;
            Assert.IsNotNull(menu, "フォルダ項目のメニュー。");
            Assert.AreEqual(1, menu.Items.Count, "「エクスプローラーで開く」だけ。");
            Assert.AreEqual(0, menu.Items.OfType<MenuItem>().Count(item => item.Name == "treeViewDuplicateFolderContextMenuItemMergeInto"));
            var keyArgs = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(folderItem), 0, Key.G)
            { RoutedEvent = UIElement.KeyDownEvent, Source = folderItem };
            folderItem.RaiseEvent(keyArgs);
            Assert.IsFalse(keyArgs.Handled, "Ctrl+G の統合・整理を呼ばない。");
            Assert.AreEqual(0, duplicateOperationCalls);

            // 結果の失効後は、受付が使用中のBusyで、案内を一件だけ出して一覧を変えない。
            var emptied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            viewModel.MainChartList.PropertyChanged += (_, args) =>
            { if (args.PropertyName == nameof(MainChartListViewModel.Rows) && viewModel.MainChartList.Rows.Count == 0) { emptied.TrySetResult(); } };
            library.BmsCharts = charts;
            TestUiDispatcherHost.AwaitTaskOnDispatcher(emptied.Task, "split placement invalidation empties list");
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.IsNull(library.SplitPlacementGroups);
            IList rows = viewModel.MainChartList.Rows;
            UiProgressRequestHolder before = new(dialogs.ProgressRequest);
            Assert.IsTrue(library.OperationAdmission.TryEnter(out IDisposable? other2));
            try
            {
                root.IsSelected = false;
                Task busyHandler = MainWindowEventCompletion.CaptureAsyncEventCompletion(() => root.IsSelected = true);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(busyHandler, "split placement busy selection handler");
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.AreEqual(1, dialogs.Messages.Count, "Busy案内は一件。");
                Assert.AreEqual(Resources.Warn_LibraryOperationBusy, dialogs.Messages[0].MessageBoxText);
                Assert.AreSame(before.Request, dialogs.ProgressRequest, "進捗を表示しない。");
                Assert.AreSame(rows, viewModel.MainChartList.Rows, "表示中の一覧を変えない。");
            }
            finally { other2!.Dispose(); }
        }, terminal, dialogService: dialogs);
    }

    /// <summary>実際の変更で結果を失効させ、一覧が空になって画面の反映が落ち着くまで進めます。</summary>
    private static void InvalidateSplitPlacementAndSettle(MainWindowViewModel viewModel, Action invalidate)
    {
        var emptied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        System.ComponentModel.PropertyChangedEventHandler changed = (_, args) =>
        { if (args.PropertyName == nameof(MainChartListViewModel.Rows) && viewModel.MainChartList.Rows.Count == 0) { emptied.TrySetResult(); } };
        viewModel.MainChartList.PropertyChanged += changed;
        try
        {
            invalidate();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(emptied.Task, "split placement invalidation empties list");
        }
        finally { viewModel.MainChartList.PropertyChanged -= changed; }
        TestUiDispatcherHost.ProcessQueuedPresentation();
        TestUiDispatcherHost.ProcessQueuedPresentation();
    }

    private static void SelectSplitPlacementRootAndSearch(TreeViewItem root)
    {
        Task handler = MainWindowEventCompletion.CaptureAsyncEventCompletion(() => root.IsSelected = true);
        TestUiDispatcherHost.AwaitTaskOnDispatcher(handler, "split placement root selection handler");
        TestUiDispatcherHost.ProcessQueuedPresentation();
    }

    private static void RaiseSplitPlacementRootClick(TreeViewItem root)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Source = root };
        Task handler = MainWindowEventCompletion.CaptureAsyncEventCompletion(() => root.RaiseEvent(args));
        TestUiDispatcherHost.AwaitTaskOnDispatcher(handler, "split placement root click handler");
    }

    private static void RaiseSplitPlacementRootEnter(TreeViewItem root)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(root), 0, Key.Enter)
        { RoutedEvent = UIElement.KeyDownEvent, Source = root };
        Task handler = MainWindowEventCompletion.CaptureAsyncEventCompletion(() => root.RaiseEvent(args));
        TestUiDispatcherHost.AwaitTaskOnDispatcher(handler, "split placement root Enter handler");
    }

    /// <summary>
    /// 保証：グループまたはフォルダを選んだ状態で結果が実際の変更で失効し、画面が親のルートを自動で選び直しても、それを利用者の選択として扱わず、
    /// 受付が空いていても他者が保持中でも、探索・進捗・Busy案内を行わない（結果はnullのまま、一覧は空）。
    /// その後、選択済みのルートのクリックで探索が始まる。検出する誤り：自動の選び直しによる探索の開始・Busy案内、選択済みのルートの再操作が無視される。
    /// </summary>
    [TestMethod]
    [DataRow("group", false)]
    [DataRow("group", true)]
    [DataRow("folder", false)]
    [DataRow("folder", true)]
    public void SplitPlacementTree_AutomaticRootReselectionAfterInvalidationDoesNotSearchOrNotify(string child, bool held)
    {
        var dialogs = new RecordingDialogService(new EventLog());
        RunDuplicateSelectionFixture((scope, viewModel, window, library, charts, navigation) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            string related = charts[0].Directory, other = charts[2].Directory;
            SetIndex(library, new(related, Keys(1, 10)), new(other, Keys(1, 10)));
            var root = (TreeViewItem)window.FindName("treeViewItemSearchSplitPlacement");
            SelectSplitPlacementRootAndSearch(root);
            DuplicateGroup group = library.SplitPlacementGroups!.Single();
            TreeViewItem item = child == "group" ? RealizeDuplicateGroupItem(root, group) : RealizeDuplicateFolderItem(root, group, related);
            Task select = MainWindowEventCompletion.CaptureAsyncEventCompletion(() => item.IsSelected = true);
            TestUiDispatcherHost.AwaitTaskOnDispatcher(select, "split placement child selection handler");
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.IsTrue(item.IsSelected);
            var progressBefore = dialogs.ProgressRequest;
            int messagesBefore = dialogs.Messages.Count;
            IDisposable? lease = null;
            if (held) { Assert.IsTrue(library.OperationAdmission.TryEnter(out lease)); }
            try
            {
                // 受付を他者が保持している間の失効は、保持している操作自身の変更（フォルダの統合）で起こす。受付が空いているときは全置換で起こす。
                InvalidateSplitPlacementAndSettle(viewModel, () =>
                {
                    if (lease == null) { library.BmsCharts = charts; return; }
                    using LibraryFileMutationCapability capability = library.OperationAdmission.CreateMutationCapability(lease);
                    Assert.IsTrue(library.MergeChartDirectory(related, charts[1].Directory, 1, new LibraryOperationNotices(), capability: capability).MergeApplied);
                });

                Assert.IsTrue(root.IsSelected, "選択中の子が消えると、画面は親のルートを自動で選び直す。");
                Assert.IsNull(library.SplitPlacementGroups, "探索しない。");
                Assert.AreSame(progressBefore, dialogs.ProgressRequest, "進捗を表示しない。");
                Assert.AreEqual(messagesBefore, dialogs.Messages.Count, "Busy案内を出さない。");
                Assert.AreEqual(0, viewModel.MainChartList.Rows.Count, "一覧は空。");
            }
            finally { lease?.Dispose(); }

            // 選択済みのルートのクリックで、探索が始まる。
            RaiseSplitPlacementRootClick(root);
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.AreNotSame(progressBefore, dialogs.ProgressRequest, "ルートのクリックで探索する。");
            Assert.IsNotNull(library.SplitPlacementGroups);
            if (!held) { Assert.AreEqual(2, viewModel.MainChartList.Rows.Count); }
        }, dialogService: dialogs);
    }

    /// <summary>
    /// 保証：有効な結果があるときの選択済みのルートのクリックとEnterは何も変えず（探索も進捗もない）、結果が失効した後の選択済みのルートでのEnterは探索を始める。
    /// 検出する誤り：選択済みのルートの再操作の無視、有効な結果での不要な探索、失効だけでの探索。
    /// </summary>
    [TestMethod]
    public void SplitPlacementTree_SelectedRootEnterSearchesOnlyWithoutCurrentResult()
    {
        var dialogs = new RecordingDialogService(new EventLog());
        RunDuplicateSelectionFixture((scope, viewModel, window, library, charts, navigation) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            SetIndex(library, new(charts[0].Directory, Keys(1, 10)), new(charts[2].Directory, Keys(1, 10)));
            var root = (TreeViewItem)window.FindName("treeViewItemSearchSplitPlacement");
            SelectSplitPlacementRootAndSearch(root);
            var firstProgress = dialogs.ProgressRequest;

            RaiseSplitPlacementRootEnter(root);
            RaiseSplitPlacementRootClick(root);
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.AreSame(firstProgress, dialogs.ProgressRequest, "有効な結果があるときは何も変えない。");

            InvalidateSplitPlacementAndSettle(viewModel, () => library.BmsCharts = charts);
            Assert.IsTrue(root.IsSelected);
            Assert.AreSame(firstProgress, dialogs.ProgressRequest, "失効だけでは探索しない。");
            RaiseSplitPlacementRootEnter(root);
            TestUiDispatcherHost.ProcessQueuedPresentation();

            Assert.AreNotSame(firstProgress, dialogs.ProgressRequest, "Enterで探索する。");
            Assert.IsNotNull(library.SplitPlacementGroups);
            Assert.AreEqual(2, viewModel.MainChartList.Rows.Count);
        }, dialogService: dialogs);
    }

    /// <summary>進捗の要求の参照を、後で同一性を比べるために保持します。</summary>
    private sealed record UiProgressRequestHolder(BeMusicSeeker.Views.Dialogs.UiProgressRequest? Request);
}
