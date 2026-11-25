using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.Tests.TreeSelectionDisplayTestSupport;
using static BeMusicSeeker.TestSupport.Components.SelectedChartAudioConversionWorkflowOwnerTestSupport;
using static BeMusicSeeker.TestSupport.Components.SplitPlacementTestSupport;
using ConversionTestSupport = BeMusicSeeker.TestSupport.Components.SelectedChartAudioConversionWorkflowOwnerTestSupport;
using DuplicateSupport = BeMusicSeeker.TestSupport.Components.DuplicateMaintenanceWorkflowOwnerTestSupport;

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
            Assert.AreEqual(2, root.Items.Count, "統合候補一覧のノードとグループ。");
            DuplicateGroup group = library.SplitPlacementGroups!.Single();
            TreeViewItem folderItem = RealizeDuplicateFolderItem(root, group, related);
            var groupItem = (TreeViewItem)root.ItemContainerGenerator.ContainerFromIndex(root.Items.IndexOf(group));
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
            UiProgressRequest? progressBefore = dialogs.ProgressRequest;
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
            UiProgressRequest? firstProgress = dialogs.ProgressRequest;

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

    // ---------------------------------------------------------------------------------------------------------------
    // 分裂配置の統合候補一覧（ノード、共用の表の束縛、重複の一覧との独立、行の活性化、実際の整理）。
    // 重複の画面の準備データ（四つのフォルダ。同じグループの二つは同一内容）を使い、別のグループのフォルダ同士が音源を共有する二つの分裂配置のグループを作る。
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 二つの分裂配置のグループの音源の関係を設定します。グループAは<c>group0-0</c>と<c>group1-0</c>、グループBは<c>group0-1</c>と<c>group1-1</c>です
    /// （同じ重複グループの二つは主ハッシュが同じため、別の重複グループのフォルダ同士で関係を作ります）。
    /// </summary>
    private static void SetSplitPairsIndex(TestBmsLibrary library, ChartFile[] charts)
        => SetIndex(library,
            new(charts[0].Directory, Keys(1, 10)), new(charts[2].Directory, Keys(1, 10)),
            new(charts[1].Directory, Keys(100, 10)), new(charts[3].Directory, Keys(100, 10)));

    /// <summary>分裂配置のルートを選んで探索し、統合候補一覧のノードを選んで、一覧の採用（表示中への遷移）まで待ちます。</summary>
    private static TreeViewItem SelectSplitPlacementCandidateList(MainWindowViewModel viewModel, TreeViewItem root, bool searchFirst)
    {
        if (searchFirst) { SelectSplitPlacementRootAndSearch(root); }
        TreeViewItem node = RealizeCandidateListNode(root);
        AwaitHandler(() => node.IsSelected = true, "split placement candidate list node selection");
        CandidateListAwaitSupport.AwaitActive(viewModel.SplitPlacementMergeCandidateList, "split placement candidate list adoption");
        TestUiDispatcherHost.ProcessQueuedPresentation();
        return node;
    }

    /// <summary>
    /// 保証：分裂配置のツリーの子の先頭に統合候補一覧のノード（リソースの見出し）が現れ、選ぶと共用の専用の表が分裂配置の一覧主体へ束縛される。
    /// 表示中は譜面一覧の表・メインの検索欄・件数表示・保留の操作バー・プレイログの集計バーを出さない。
    /// 検索欄・除外欄・SELECTのセル・簡易チェックのチェック・優先統合先は分裂配置の一覧主体にだけ作用し、重複の一覧へ切り替えると重複の値、戻ると分裂配置の値が現れる。
    /// 検出する誤り：ノードの欠落・位置・見出し、重複の一覧主体への束縛の残り、二つの一覧の状態の共有、切替えでの値の取り違え、ライブラリの交換での状態の残存。
    /// </summary>
    [TestMethod]
    public void SplitPlacementCandidateList_SharedTableFollowsTheDisplayedListAndKeepsBothStatesIndependent()
    {
        // 簡易チェックを入れる操作の警告だけを記録する。
        var dialogs = new RecordingPreferredDestinationDialogs { Messages = [] };
        RunDuplicateSelectionFixture((scope, viewModel, window, library, charts, navigation) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            SetSplitPairsIndex(library, charts);
            var splitRoot = (TreeViewItem)window.FindName("treeViewItemSearchSplitPlacement");
            var duplicateRoot = (TreeViewItem)window.FindName("treeViewItemSearchDuplicated");
            var main = (CustomTableView)window.FindName("customTableView");
            var table = (CustomTableView)window.FindName("customTableDuplicateCandidates");
            var search = (Border)window.FindName("KeywordSearchComposite");
            var candidateSearch = (Border)window.FindName("duplicateCandidateSearchComposite");
            var candidateEditor = (KeywordSearchEditor)window.FindName("duplicateCandidateSearchEditor");
            var excludeEditor = (KeywordSearchEditor)window.FindName("duplicateCandidateExcludeEditor");
            var summary = (TextBlock)window.FindName("duplicateCandidateSummaryText");
            var quickCompare = (CheckBox)window.FindName("duplicateCandidateQuickCompareCheckBox");
            var pendingBar = (Border)window.FindName("pendingInstallOperationBar");
            var historyBar = (Border)window.FindName("playHistorySummaryBar");
            TextBlock mainSummary = FindMainSummaryText(window);
            DuplicateMergeCandidateListOwner split = viewModel.SplitPlacementMergeCandidateList;
            DuplicateMergeCandidateListOwner duplicate = viewModel.DuplicateMergeCandidateList;
            Assert.AreNotSame(split, duplicate);

            // ノード：結果がある間、子の先頭に、重複のノードとは別の項目として現れる。
            SelectSplitPlacementRootAndSearch(splitRoot);
            Assert.AreEqual(3, splitRoot.Items.Count, "ノードと二つのグループ項目");
            Assert.AreSame(viewModel.MaintenanceTree.SplitPlacementCandidateListNodeItem, splitRoot.Items[0], "ノードは先頭");
            Assert.AreNotSame(viewModel.MaintenanceTree.DuplicateMergeCandidateListNodeItem, splitRoot.Items[0]);
            Assert.IsInstanceOfType<DuplicateGroup>(splitRoot.Items[1]);
            TreeViewItem node = RealizeCandidateListNode(splitRoot);
            Assert.AreEqual(Resources.Duplicate_merge_candidate_list, node.Header);
            Assert.IsTrue(viewModel.DisplaysSplitPlacementCandidateList, "分裂配置の画面種別");

            SelectSplitPlacementCandidateList(viewModel, splitRoot, searchFirst: false);
            Assert.AreSame(split, viewModel.DisplayedCandidateList);
            Assert.AreSame(split, table.DataContext, "共用の表は分裂配置の一覧主体へ束縛される");
            Assert.AreEqual(Visibility.Visible, table.Visibility);
            Assert.AreEqual(Visibility.Collapsed, main.Visibility, "譜面一覧の表は出さない");
            Assert.AreEqual(Visibility.Collapsed, search.Visibility, "メインの検索欄は出さない");
            AssertCandidateListHidesMainBars(mainSummary, pendingBar, historyBar);
            Assert.AreEqual(0, viewModel.MainChartList.Rows.Count, "譜面一覧は行0件");
            Assert.AreEqual(-1, viewModel.MainChartList.SelectedIndex);
            Assert.AreEqual(4, table.ItemsSource.Count);
            Assert.AreEqual(string.Format(Resources.Duplicate_merge_candidate_summary, 2, 0), summary.Text, "件数表示は分裂配置の一覧の値");
            Assert.IsFalse(duplicate.IsViewActive);

            // 分裂配置の一覧の操作：セル・検索欄・除外欄・簡易チェック・優先統合先は、分裂配置の一覧主体にだけ作用する。
            Click(table, 0, "Select");
            Assert.AreEqual(1, split.SelectedGroupCount);
            Assert.AreEqual(string.Format(Resources.Duplicate_merge_candidate_summary, 2, 1), summary.Text);
            EnterCandidateText(split, candidateEditor, "group0-0", nameof(DuplicateMergeCandidateListOwner.SearchText), () => split.SearchText);
            EnterCandidateText(split, excludeEditor, "group1-1", nameof(DuplicateMergeCandidateListOwner.ExcludeText), () => split.ExcludeText);
            split.RequestSort("Path", System.ComponentModel.ListSortDirection.Descending);
            Assert.IsFalse(quickCompare.IsChecked, "前提：簡易チェックは既定で外れています。");
            quickCompare.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true); // 利用者のクリックと同じく、束縛を残して現在値を変える
            AwaitHandler(() => quickCompare.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)), "split quick compare on");
            Assert.IsTrue(split.QuickCompareEnabled);
            Assert.AreEqual(1, dialogs.Messages!.Count, "入れる操作の警告が一件です。");
            Assert.AreEqual(Resources.Duplicate_merge_candidate_quick_compare_notice, dialogs.Messages[0].MessageBoxText);
            string preferred = charts[2].Directory;
            dialogs.NextResult = new UiWindowDialogResult<IReadOnlyList<string>>(UiDialogStatus.Accepted, [preferred], true);
            ClickButton(window, "duplicateCandidatePreferredDestinationsButton");
            CollectionAssert.AreEqual(new[] { preferred }, split.PreferredDestinations.ToArray());
            Assert.AreEqual(preferred, split.RowSet.First(row => row.Path == preferred).State.KeepFolder, "優先統合先が分裂配置の一覧のKEEPへ適用される");

            // 重複の一覧へ切り替える：重複の一覧主体の値（独立した状態）が現れ、分裂配置の一覧の値は変わらない。
            SelectCandidateListAndAwaitView(viewModel, navigation, RealizeCandidateListNode(duplicateRoot));
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.AreSame(duplicate, viewModel.DisplayedCandidateList);
            Assert.AreSame(duplicate, table.DataContext);
            Assert.IsFalse(split.IsViewActive, "分裂配置の一覧は表示中でない");
            Assert.AreEqual(string.Empty, candidateEditor.Text, "重複の一覧の検索語");
            Assert.AreEqual(string.Empty, excludeEditor.Text);
            Assert.IsNull(duplicate.SortColumnName);
            Assert.AreEqual(0, duplicate.PreferredDestinations.Count);
            Assert.IsFalse(quickCompare.IsChecked, "重複の一覧の簡易チェックは既定のまま");
            Assert.IsFalse(duplicate.QuickCompareEnabled);
            Assert.AreEqual(4, table.ItemsSource.Count);
            Assert.AreEqual(string.Format(Resources.Duplicate_merge_candidate_summary, 2, 0), summary.Text, "重複の一覧の件数");
            Click(table, 2, "Select");
            Assert.AreEqual(1, duplicate.SelectedGroupCount);
            Assert.AreEqual(1, split.SelectedGroupCount, "重複の一覧の操作で分裂配置の一覧は変わらない");
            Assert.AreEqual("group0-0", split.SearchText);
            Assert.AreEqual("group1-1", split.ExcludeText);
            Assert.AreEqual("Path", split.SortColumnName);
            Assert.IsTrue(split.QuickCompareEnabled);

            // 分裂配置の一覧へ戻る：分裂配置の値が現れる（重複の一覧の値は変わらない）。
            SelectSplitPlacementCandidateList(viewModel, splitRoot, searchFirst: false);
            Assert.AreSame(split, viewModel.DisplayedCandidateList);
            Assert.AreSame(split, table.DataContext);
            Assert.AreEqual("group0-0", candidateEditor.Text);
            Assert.AreEqual("group1-1", excludeEditor.Text);
            Assert.IsTrue(quickCompare.IsChecked);
            Assert.AreEqual(1, split.SelectedGroupCount);
            Assert.AreEqual(1, duplicate.SelectedGroupCount);
            Assert.AreEqual(0, duplicate.PreferredDestinations.Count);
            Assert.IsFalse(duplicate.QuickCompareEnabled);
            Assert.AreEqual(1, dialogs.Messages.Count, "切替えでは警告を出しません。");

            // ライブラリの交換：分裂配置の一覧のグループの状態も破棄される。
            DuplicateGroup group = library.SplitPlacementGroups![0];
            Assert.IsTrue(split.TryGetState(group, out _));
            split.AttachLibrary(new object());
            Assert.IsFalse(split.TryGetState(group, out _), "ライブラリの交換でグループの状態を破棄する");
            Assert.IsFalse(split.IsViewActive);
        }, dialogService: dialogs);
    }

    /// <summary>
    /// 保証：コンパイル済みの統合候補一覧のツールバーで、探索条件の行は分裂配置の一覧の表示中だけ表示され（重複の一覧では非表示）、
    /// 三つの入力は選択式（共有率は50〜100の5刻み、汎用名とみなすフォルダ数は2〜100、基準は二つの選択肢でリソースの文言）で、「再探索」はリソースの文言である。
    /// 実際の入力で入力中の値だけが変わり（適用中の探索条件・一覧は変わらない）、「再探索」が強調され、画面の行き来の後も入力中の値を表示する。
    /// 0件になる条件での「再探索」は、進捗の所有者がメインウィンドウで、適用中の探索条件を更新し、ノードが残って選択されたまま表は表示中で行0件になり、探索条件の行も表示中で、強調が解ける。
    /// 検出する誤り：探索条件の行の表示条件の取り違え、選択肢の欠落・不正な値の入力、入力だけでの適用、強調の欠落や解除漏れ、行き来での入力の巻き戻し、0件での一覧の終了・ノードの除去。
    /// </summary>
    [TestMethod]
    public void SplitPlacementCandidateList_CriteriaRowAdjustsInputAndResearchKeepsTheListDisplayed()
    {
        var dialogs = new RecordingDialogService(new EventLog());
        RunDuplicateSelectionFixture((scope, viewModel, window, library, charts, navigation) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            SetSplitPairsIndex(library, charts);
            var splitRoot = (TreeViewItem)window.FindName("treeViewItemSearchSplitPlacement");
            var duplicateRoot = (TreeViewItem)window.FindName("treeViewItemSearchDuplicated");
            var table = (CustomTableView)window.FindName("customTableDuplicateCandidates");
            var criteriaRow = (StackPanel)window.FindName("splitPlacementCriteriaRow");
            var shareCombo = (ComboBox)window.FindName("splitPlacementSharePercentComboBox");
            var limitCombo = (ComboBox)window.FindName("splitPlacementGenericFolderLimitComboBox");
            var basisCombo = (ComboBox)window.FindName("splitPlacementShareBasisComboBox");
            var researchButton = (Button)window.FindName("splitPlacementResearchButton");
            SplitPlacementSearchWorkflowOwner search = viewModel.SplitPlacementSearch;
            DuplicateMergeCandidateListOwner split = viewModel.SplitPlacementMergeCandidateList;

            SelectSplitPlacementCandidateList(viewModel, splitRoot, searchFirst: true);

            // 表示と選択肢。
            Assert.AreEqual(Visibility.Visible, criteriaRow.Visibility, "分裂配置の一覧では探索条件の行を表示する");
            CollectionAssert.AreEqual(new[] { 50, 55, 60, 65, 70, 75, 80, 85, 90, 95, 100 }, shareCombo.Items.Cast<int>().ToArray());
            CollectionAssert.AreEqual(Enumerable.Range(2, 99).ToArray(), limitCombo.Items.Cast<int>().ToArray());
            CollectionAssert.AreEqual(
                new[] { Resources.Split_placement_criteria_basis_smaller, Resources.Split_placement_criteria_basis_both },
                basisCombo.Items.Cast<ComboBoxItem>().Select(item => item.Content).ToArray());
            Assert.AreEqual(Resources.Split_placement_research, researchButton.Content);
            Assert.IsFalse(shareCombo.IsEditable || limitCombo.IsEditable || basisCombo.IsEditable, "選択式で、範囲の外の値を入力できない");
            Assert.AreEqual(SplitPlacementSearchCriteria.Default.SharePercent, shareCombo.SelectedItem, "初期表示は既定値から導く");
            // 共通のテンプレートは選択欄へ書式を渡さないため、閉じた状態の表示にも「%」が付くことを実際の表示要素で確かめる。
            shareCombo.ApplyTemplate();
            shareCombo.UpdateLayout();
            var shareSelection = (ContentPresenter)shareCombo.Template.FindName("SelectionContent", shareCombo);
            Assert.AreEqual("95%", ((TextBlock)System.Windows.Media.VisualTreeHelper.GetChild(shareSelection, 0)).Text,
                "共有率は、閉じた状態でも「%」を付けて表示する");
            Assert.AreEqual(SplitPlacementSearchCriteria.Default.GenericFolderLimit, limitCombo.SelectedItem);
            Assert.AreEqual(SplitPlacementSearchCriteria.Default.Basis, basisCombo.SelectedValue);
            Assert.AreEqual("10", ((TextBlock)System.Windows.Media.VisualTreeHelper.GetChild(
                (ContentPresenter)limitCombo.Template.FindName("SelectionContent", limitCombo), 0)).Text, "初期表示の汎用名とみなすフォルダ数は「10」");
            Assert.AreEqual(Resources.Split_placement_criteria_basis_smaller, basisCombo.Text, "初期表示の基準は「音源の少ない方」の文言");
            Assert.AreNotEqual(FontWeights.Bold, researchButton.FontWeight, "入力が適用中と同じ間は強調しない");

            // 実際の入力：入力中の値だけが変わり、適用中の探索条件と一覧は変わらず、「再探索」が強調される。
            List<DuplicateGroup> resultBefore = library.SplitPlacementGroups!;
            shareCombo.SelectedItem = 100;
            limitCombo.SelectedItem = 30;
            basisCombo.SelectedValue = SplitPlacementShareBasis.BothSides;
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.AreEqual(new SplitPlacementSearchCriteria(100, 30, SplitPlacementShareBasis.BothSides), search.InputCriteria);
            Assert.AreEqual(SplitPlacementSearchCriteria.Default, search.AppliedCriteria, "入力だけでは適用しない");
            Assert.AreSame(resultBefore, library.SplitPlacementGroups);
            Assert.AreEqual(4, table.ItemsSource.Count, "一覧は変わらない");
            Assert.AreEqual(FontWeights.Bold, researchButton.FontWeight, "入力が適用中と異なる間は強調する");

            // 重複の一覧では探索条件の行を表示せず、戻ると入力中の値と強調が保たれる。
            SelectCandidateListAndAwaitView(viewModel, navigation, RealizeCandidateListNode(duplicateRoot));
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.AreEqual(Visibility.Collapsed, criteriaRow.Visibility, "重複の一覧では探索条件の行を表示しない");
            SelectSplitPlacementCandidateList(viewModel, splitRoot, searchFirst: false);
            Assert.AreEqual(Visibility.Visible, criteriaRow.Visibility);
            Assert.AreEqual(100, shareCombo.SelectedItem, "画面の行き来の後も入力中の値を表示する");
            Assert.AreEqual(30, limitCombo.SelectedItem);
            Assert.AreEqual(SplitPlacementShareBasis.BothSides, basisCombo.SelectedValue);
            Assert.AreEqual(FontWeights.Bold, researchButton.FontWeight);

            // 0件になる条件での「再探索」：一覧の表示とノードの選択を保つ。
            SetIndex(library,
                new(charts[0].Directory, Keys(1, 10)), new(charts[2].Directory, Keys(200, 10)),
                new(charts[1].Directory, Keys(100, 10)), new(charts[3].Directory, Keys(300, 10)));
            AwaitHandler(() => ClickButton(window, "splitPlacementResearchButton"), "split placement research with no groups");
            TestUiDispatcherHost.ProcessQueuedPresentation();

            Assert.AreSame(window, dialogs.ProgressRequest!.Owner, "進捗画面の所有者はメインウィンドウ");
            Assert.AreEqual(0, library.SplitPlacementGroups!.Count);
            Assert.AreEqual(search.InputCriteria, search.AppliedCriteria, "適用中の探索条件が入力中の値になる");
            Assert.AreEqual(1, splitRoot.Items.Count, "ノードだけが残る");
            Assert.IsTrue(RealizeCandidateListNode(splitRoot).IsSelected, "ノードの選択を保つ");
            Assert.IsTrue(split.IsViewActive, "一覧の表示を保つ");
            Assert.AreEqual(Visibility.Visible, table.Visibility);
            Assert.AreEqual(0, table.ItemsSource.Count, "表は行0件");
            Assert.AreEqual(Visibility.Visible, criteriaRow.Visibility, "探索条件の行も表示中");
            Assert.AreNotEqual(FontWeights.Bold, researchButton.FontWeight, "適用により強調が解ける");
        }, dialogService: dialogs);
    }

    /// <summary>
    /// 保証：分裂配置の一覧のツールバーの確認と右クリックの確認は、実際の検査器を実フォルダへ動かし、結果を分裂配置の一覧主体にだけ書く。
    /// 「別のグループを巻き込む」の基準は分裂配置の一覧の全グループで、重複検索のグループ（同じフォルダを別の組み合わせで含む）は基準に入らない。
    /// 検出する誤り：確認結果の重複の一覧主体への書込み、重複検索のグループを基準にした統合不可・要注意の誤判定、メニューの確認の行き先の取り違え。
    /// </summary>
    [TestMethod]
    public void SplitPlacementCandidateList_VerificationWritesOnlyTheSplitPlacementListUsingItsOwnGroupsAsBaseline()
    {
        var events = new ConversionTestSupport.EventLog();
        var dialogs = new ConversionTestSupport.RecordingDialogService(events);
        RunDuplicateSelectionFixture((scope, viewModel, window, library, charts, navigation) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            SetSplitPairsIndex(library, charts);
            var splitRoot = (TreeViewItem)window.FindName("treeViewItemSearchSplitPlacement");
            var duplicateRoot = (TreeViewItem)window.FindName("treeViewItemSearchDuplicated");
            var table = (CustomTableView)window.FindName("customTableDuplicateCandidates");
            var menu = (ContextMenu)window.FindResource("duplicateCandidateContextMenu");
            DuplicateMergeCandidateListOwner split = viewModel.SplitPlacementMergeCandidateList;
            DuplicateMergeCandidateListOwner duplicate = viewModel.DuplicateMergeCandidateList;
            // 重複の一覧も採用しておき、確認の前後で状態が変わらないことを確かめる。
            SelectCandidateListAndAwaitView(viewModel, navigation, RealizeCandidateListNode(duplicateRoot));
            Assert.IsTrue(duplicate.RowSet.All(row => row.State.Verification.Stage == DuplicateMergeVerificationStage.Unverified));

            SelectSplitPlacementCandidateList(viewModel, splitRoot, searchFirst: true);
            DuplicateMergeCandidateRow[] rows = [.. split.RowSet];
            Assert.AreEqual(4, rows.Length);
            Click(table, 0, "Select");
            AwaitHandler(() => ClickButton(window, "duplicateCandidateVerifyButton"), "split placement verify toolbar");
            Assert.AreEqual(DuplicateMergeVerificationStage.Safe, rows[0].State.Verification.Stage,
                "分裂配置の一覧の全グループを基準にするため、重複検索のグループと重なるフォルダでも巻き込みにならない");
            Assert.IsFalse(rows[0].State.IsSelected, "確認を終えたグループのSELECTはオフ");
            Assert.AreEqual(DuplicateMergeVerificationStage.Unverified, rows[2].State.Verification.Stage, "選択していないグループは確認しない");
            Assert.AreEqual(Resources.Duplicate_merge_candidate_verify_progress_title, dialogs.ProgressRequest!.Title);
            Assert.AreSame(window, dialogs.ProgressRequest.Owner);
            Assert.AreEqual(1, dialogs.Messages.Count(message => message.Caption == dialogs.Messages[^1].Caption), "結果の案内は一件");

            // 右クリックの確認：選択行のグループを確認する。
            table.SelectRowsByPredicate(row => row is DuplicateMergeCandidateRow { Index: 2 });
            MenuItem verifyGroup = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, Resources.Duplicate_merge_candidate_verify_group));
            AwaitHandler(() => RaiseMenuClick(verifyGroup), "split placement verify menu");
            Assert.AreEqual(DuplicateMergeVerificationStage.Safe, rows[2].State.Verification.Stage);

            Assert.IsTrue(duplicate.RowSet.All(row => row.State.Verification.Stage == DuplicateMergeVerificationStage.Unverified),
                "重複の一覧の確認結果は変わらない");
            Assert.IsFalse(duplicate.IsViewActive);
        }, dialogService: dialogs);
    }

    /// <summary>
    /// 保証：探索条件の三つの入力は、ja-JPとen-USのそれぞれで、すべての選択肢を選んだ閉じた状態の表示が実際に配置された幅に切れずに収まり、
    /// 入力の幅は選択中の値によらず等しい。基準の入力は、en-USの文言がja-JPより長く、表示言語を切り替えると新しい文言に合わせて広がる。
    /// 検出する誤り：固定の幅による選択肢の欠け、選択中の値で変わる幅、表示言語の切替への非追従、共有率の「%」の欠け。
    /// </summary>
    [TestMethod]
    public void SplitPlacementCandidateList_CriteriaInputsFitTheirLongestChoiceAndKeepOneWidthAcrossSelectionsAndLanguages()
    {
        var dialogs = new RecordingDialogService(new EventLog());
        RunDuplicateSelectionFixture((scope, viewModel, window, library, charts, navigation) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            SetSplitPairsIndex(library, charts);
            var splitRoot = (TreeViewItem)window.FindName("treeViewItemSearchSplitPlacement");
            SelectSplitPlacementCandidateList(viewModel, splitRoot, searchFirst: true);
            (string Name, ComboBox Combo)[] inputs =
            [
                ("共有率", (ComboBox)window.FindName("splitPlacementSharePercentComboBox")),
                ("汎用名とみなすフォルダ数", (ComboBox)window.FindName("splitPlacementGenericFolderLimitComboBox")),
                ("共有率の基準", (ComboBox)window.FindName("splitPlacementShareBasisComboBox"))
            ];
            var basisWidthByCulture = new Dictionary<string, double>();
            string previous = Resources.Culture?.Name ?? "ja-JP";
            try
            {
                foreach (string culture in new[] { "ja-JP", "en-US" })
                {
                    ResourceService.Current.ChangeCulture(culture);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    foreach ((string name, ComboBox combo) in inputs)
                    {
                        double? width = null;
                        for (int index = 0; index < combo.Items.Count; index++)
                        {
                            combo.SelectedIndex = index;
                            window.UpdateLayout();
                            double actual = combo.ActualWidth;
                            Assert.IsTrue(actual > 0d, $"{culture} {name} #{index}: 配置されている");
                            width ??= actual;
                            Assert.AreEqual(width.Value, actual, 0.5, $"{culture} {name} #{index}: 幅は選択中の値によらず等しい");
                            var selection = (ContentPresenter)combo.Template.FindName("SelectionContent", combo);
                            var shown = (TextBlock)System.Windows.Media.VisualTreeHelper.GetChild(selection, 0);
                            // 閉じた状態の表示が収まる幅は、枠・矢印の列（30）・余白を除いた配置済みの幅です。表示の文言を同じ書体で測り直して比べます。
                            double available = actual - combo.BorderThickness.Left - combo.BorderThickness.Right - 30d - combo.Padding.Left - combo.Padding.Right;
                            var probe = new TextBlock
                            {
                                Text = shown.Text,
                                FontFamily = shown.FontFamily,
                                FontSize = shown.FontSize,
                                FontStyle = shown.FontStyle,
                                FontWeight = shown.FontWeight,
                                FontStretch = shown.FontStretch
                            };
                            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                            Assert.IsTrue(probe.DesiredSize.Width <= available + 0.5,
                                $"{culture} {name} #{index}: 「{shown.Text}」（{probe.DesiredSize.Width}）が配置された幅（{available}）に収まる");
                            if (combo == inputs[0].Combo) { Assert.IsTrue(shown.Text.EndsWith('%'), "共有率は閉じた状態でも「%」を付けて表示する"); }
                        }
                        if (combo == inputs[2].Combo) { basisWidthByCulture[culture] = width!.Value; }
                    }
                }
                Assert.IsTrue(basisWidthByCulture["en-US"] > basisWidthByCulture["ja-JP"],
                    $"基準の入力は、長くなった文言に合わせて広がる（ja-JP {basisWidthByCulture["ja-JP"]}、en-US {basisWidthByCulture["en-US"]}）");
            }
            finally { ResourceService.Current.ChangeCulture(previous); }
        }, dialogService: dialogs);
    }

    /// <summary>行（グループのフォルダ）のダブルクリックまたはEnterの後に、メインの譜面一覧が期待のパス集合を表示するまで待ちます。</summary>
    private static void AwaitMainRows(MainWindowViewModel viewModel, MainWindow window, string[] expectedPaths)
    {
        var main = (CustomTableView)window.FindName("customTableView");
        var bound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool Matches() => ReferenceEquals(main.ItemsSource, viewModel.MainChartList.Rows)
            && viewModel.MainChartList.Rows.Cast<LibraryChartRow>().Select(row => row.Chart.Path).Order(StringComparer.Ordinal)
                .SequenceEqual(expectedPaths.Order(StringComparer.Ordinal));
        var itemsSource = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(CustomTableView.ItemsSourceProperty, typeof(CustomTableView));
        EventHandler changed = (_, _) => { if (Matches()) { bound.TrySetResult(); } };
        itemsSource.AddValueChanged(main, changed);
        try
        {
            if (Matches()) { bound.TrySetResult(); }
            TestUiDispatcherHost.AwaitPresentationOnDispatcher(bound.Task, "split placement folder rows");
        }
        finally { itemsSource.RemoveValueChanged(main, changed); }
    }

    /// <summary>
    /// 保証：分裂配置の一覧の行のダブルクリック（有効なSELECT・KEEP以外のセル）とEnterは、分裂配置のツリーの対応するグループを展開してフォルダノードを選び、
    /// そのフォルダの譜面一覧へ移る。同じパスが重複のツリーにあっても、重複のツリーのノードは選ばない。有効なSELECTのダブルクリックは遷移しない。
    /// 検出する誤り：重複のツリーのノードの選択、グループ全体の選択、遷移しない行、SELECTのダブルクリックでの遷移。
    /// </summary>
    [TestMethod]
    public void SplitPlacementCandidateList_RowActivationSelectsTheFolderNodeInTheSplitPlacementTree()
    {
        var dialogs = new RecordingDialogService(new EventLog());
        RunDuplicateSelectionFixture((scope, viewModel, window, library, charts, navigation) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            SetSplitPairsIndex(library, charts);
            var splitRoot = (TreeViewItem)window.FindName("treeViewItemSearchSplitPlacement");
            var duplicateRoot = (TreeViewItem)window.FindName("treeViewItemSearchDuplicated");
            var table = (CustomTableView)window.FindName("customTableDuplicateCandidates");
            DuplicateMergeCandidateListOwner split = viewModel.SplitPlacementMergeCandidateList;
            SelectSplitPlacementCandidateList(viewModel, splitRoot, searchFirst: true);
            DuplicateMergeCandidateRow target = split.RowSet[3];
            string expected = charts.Single(chart => string.Equals(chart.Directory, target.Path, StringComparison.OrdinalIgnoreCase)).Path;
            Assert.IsTrue(library.DuplicateChartGroups.Any(group => group.Folders.Contains(target.Path, StringComparer.OrdinalIgnoreCase)),
                "前提：同じパスが重複のツリーにもある");

            // 有効なSELECTのセルのダブルクリックは遷移しない。
            CustomTableHitTestResult selectHit = HitCell(table, target.Index, "Select", out Point selectPoint);
            Assert.IsTrue(table.HandleCellMouseDown(selectHit, MouseButton.Left, ModifierKeys.None, 1, selectPoint));
            Assert.IsTrue(table.HandleCellMouseDown(selectHit, MouseButton.Left, ModifierKeys.None, 2, selectPoint));
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.IsTrue(split.IsViewActive, "SELECTのダブルクリックは遷移しない");

            // PATH列のダブルクリック：フォルダノードを選び、そのフォルダの譜面一覧へ移る。
            table.SelectedIndex = target.Index;
            CustomTableHitTestResult hit = HitCell(table, target.Index, "Path", out Point point);
            AwaitHandler(() =>
            {
                Assert.IsTrue(table.HandleCellMouseDown(hit, MouseButton.Left, ModifierKeys.None, 1, point));
                Assert.IsTrue(table.HandleCellMouseDown(hit, MouseButton.Left, ModifierKeys.None, 2, point));
            }, "split placement row double click");
            AwaitMainRows(viewModel, window, [expected]);
            TestUiDispatcherHost.ProcessQueuedPresentation();
            AssertSplitFolderNodeSelected(splitRoot, duplicateRoot, library, target);
            Assert.IsFalse(split.IsViewActive, "フォルダの譜面一覧へ移り、専用の表を隠す");
            Assert.AreEqual(Visibility.Visible, ((CustomTableView)window.FindName("customTableView")).Visibility);

            // Enter：一覧へ戻って、別の行で同じ遷移をする。
            SelectSplitPlacementCandidateList(viewModel, splitRoot, searchFirst: false);
            DuplicateMergeCandidateRow other = split.RowSet[0];
            string otherExpected = charts.Single(chart => string.Equals(chart.Directory, other.Path, StringComparison.OrdinalIgnoreCase)).Path;
            AwaitHandler(() => InvokeRowActivated(window, table, other), "split placement row Enter");
            AwaitMainRows(viewModel, window, [otherExpected]);
            TestUiDispatcherHost.ProcessQueuedPresentation();
            AssertSplitFolderNodeSelected(splitRoot, duplicateRoot, library, other);
        }, dialogService: dialogs);
    }

    private static void AssertSplitFolderNodeSelected(
        TreeViewItem splitRoot, TreeViewItem duplicateRoot, TestBmsLibrary library, DuplicateMergeCandidateRow row)
    {
        DuplicateGroup group = row.State.Group;
        var groupItem = (TreeViewItem)splitRoot.ItemContainerGenerator.ContainerFromItem(group);
        Assert.IsNotNull(groupItem, "対応するグループの項目");
        Assert.IsTrue(groupItem.IsExpanded, "グループを展開する");
        TreeViewItem folderItem = FindDescendants<TreeViewItem>(groupItem)
            .Single(item => item.DataContext is string path && string.Equals(path, row.Path, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(folderItem.IsSelected, "フォルダノードを選ぶ");
        Assert.IsFalse(groupItem.IsSelected, "グループ全体は選ばない");
        Assert.IsTrue(FindDescendants<TreeViewItem>(duplicateRoot).All(item => !item.IsSelected), "重複のツリーのノードは選ばない");
        Assert.IsTrue(library.SplitPlacementGroups!.Contains(group));
    }

    /// <summary>
    /// 保証：実画面の「選択グループを整理」ボタンから、分裂配置の一覧の対象に未確認のグループがあると、案内を一件だけ出して何も始めない。
    /// 確認済み（安全）にして整理すると、実ファイル・SQLiteで統合し、整理後の有効な結果がないため同じ受付の中で探索し直す。
    /// 分裂配置の統合候補一覧を表示したまま作り直し（別の画面へ移らない）、解消したグループの行は消え、引き継いだグループは同じインスタンスでSELECT・KEEP・確認結果を保つ。
    /// 重複結果は明示的に準備されない。結果は一回だけ案内し、Busy案内は出ず、受付と活動を解放する。
    /// 整理の前後で、引き継ぐはずのグループ（グループB）が存在することを前提として表明する。
    /// 検出する誤り：未確認の整理の続行・案内の欠落や重複、再探索の欠落、状態の消失・インスタンスの置換、別の画面への移動、重複結果の明示準備、解放漏れ。
    /// </summary>
    [TestMethod]
    public void SplitPlacementCandidateList_OrganizeButtonRejectsUnverifiedThenMergesAndRebuildsTheList()
    {
        var dialogs = new DuplicateSupport.FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
        };
        RunDuplicateSelectionFixture((scope, viewModel, window, library, charts, navigation) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            SetSplitPairsIndex(library, charts);
            var splitRoot = (TreeViewItem)window.FindName("treeViewItemSearchSplitPlacement");
            var table = (CustomTableView)window.FindName("customTableDuplicateCandidates");
            var main = (CustomTableView)window.FindName("customTableView");
            DuplicateMergeCandidateListOwner split = viewModel.SplitPlacementMergeCandidateList;
            ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(viewModel);
            SelectSplitPlacementCandidateList(viewModel, splitRoot, searchFirst: true);
            // 既定値以外の探索条件を、実際の入力と「再探索」ボタンで適用しておく（整理の後の探索し直しが、この値を使うことの前提）。
            var shareCombo = (ComboBox)window.FindName("splitPlacementSharePercentComboBox");
            shareCombo.SelectedItem = 75;
            AwaitHandler(() => ClickButton(window, "splitPlacementResearchButton"), "split placement research with a non default criteria");
            CandidateListAwaitSupport.AwaitActive(split, "split placement candidate list rebuilt by research");
            TestUiDispatcherHost.ProcessQueuedPresentation();
            SplitPlacementSearchCriteria applied = viewModel.SplitPlacementSearch.AppliedCriteria;
            Assert.AreEqual(75, applied.SharePercent, "前提：既定値以外を適用している");
            Assert.IsTrue(library.HasCurrentSplitPlacementGroups(applied));
            Assert.IsFalse(library.HasCurrentSplitPlacementGroups(SplitPlacementSearchCriteria.Default), "前提：結果は適用中の探索条件のもの");
            DuplicateMergeCandidateRow[] rows = [.. split.RowSet];
            Assert.AreEqual(4, rows.Length, "前提：二つのグループ（AとB）が表示される");
            DuplicateMergeCandidateGroupState groupA = rows[0].State, groupB = rows[2].State;
            Assert.AreNotSame(groupA, groupB);
            List<DuplicateGroup> resultBefore = library.SplitPlacementGroups!;
            string keepA = rows[0].Path, sourceA = rows[1].Path;
            int progressRuns = dialogs.Progress.Runs;

            // グループBに目印（KEEPの変更と要注意の確認結果）を付け、整理しない側にする。
            Click(table, 3, "Keep");
            var cautionResult = new DuplicateMergeVerificationResult(
                DuplicateMergeVerificationStage.Caution,
                [new DuplicateMergeVerificationReason(DuplicateMergeVerificationReasonKind.ParentReference, [new DuplicateMergeFinding(rows[2].Path, "c.bms", "../x.wav")])]);
            split.WriteVerifications([new DuplicateMergeVerificationWrite(groupB.Group, cautionResult)]);
            string keepB = groupB.KeepFolder!;
            Assert.AreEqual(rows[3].Path, keepB, "前提：グループBのKEEPを変えている");

            // 未確認のグループが対象：案内を一件だけ出して何も始めない。
            Click(table, 0, "Select");
            Assert.AreEqual(1, split.SelectedGroupCount);
            AwaitHandler(() => ClickButton(window, "duplicateCandidateOrganizeButton"), "split placement organize with unverified target");
            Assert.AreEqual(Resources.Split_placement_batch_organize_unverified_notice, dialogs.Messages.Single().MessageBoxText, "新しい案内が一件だけ");
            Assert.IsNull(dialogs.ConfirmationRequest, "確認を出さない");
            Assert.AreEqual(progressRuns, dialogs.Progress.Runs, "進捗を開かない");
            Assert.IsTrue(composition.OperationAdmission.CanEnter, "受付を取らない");
            Assert.IsTrue(Directory.Exists(sourceA), "何も変えない");
            Assert.AreSame(resultBefore, library.SplitPlacementGroups);
            Assert.IsTrue(groupA.IsSelected && ReferenceEquals(DuplicateMergeVerificationResult.Unverified, groupA.Verification));

            // 確認済み（安全）にして整理する。
            var safe = new DuplicateMergeVerificationResult(DuplicateMergeVerificationStage.Safe, []);
            split.WriteVerifications([new DuplicateMergeVerificationWrite(groupA.Group, safe)]);
            Assert.IsFalse(groupA.IsSelected, "安全を書き込んでも、SELECTはオフ（自動でオンにしない）");
            Click(table, 0, "Select");
            Assert.IsTrue(groupA.IsSelected, "安全の確認の後に、利用者が選択し直す");
            Assert.IsFalse(groupB.IsSelected);
            bool? gateFreeAtResult = null;
            dialogs.OnMessage = () => gateFreeAtResult ??= composition.OperationAdmission.CanEnter && !viewModel.ChartMutationActivity.IsActive;
            Task handler = MainWindowEventCompletion.CaptureAsyncEventCompletion(() => ClickButton(window, "duplicateCandidateOrganizeButton"));
            TestUiDispatcherHost.AwaitTaskOnDispatcher(handler, "split placement organize handler terminal");
            AwaitCandidateRows(split, 2);
            TestUiDispatcherHost.ProcessQueuedPresentation();

            // 一覧：別の画面へ移らず作り直し、解消したグループの行は消え、引き継いだグループは同じインスタンスで状態を保つ。
            Assert.IsTrue(split.IsViewActive, "別の画面へ自動で移らず、一覧を表示したまま");
            DuplicateMergeCandidateRow[] after = [.. split.RowSet];
            CollectionAssert.AreEqual(new[] { rows[2].Path, rows[3].Path }, after.Select(row => row.Path).ToArray(), "解消したグループの行は消える");
            Assert.AreSame(groupB, after[0].State, "引き継いだグループは同じ状態のインスタンス");
            Assert.AreEqual(keepB, groupB.KeepFolder, "KEEPを保つ");
            Assert.IsFalse(groupB.IsSelected, "対象でなかったグループのSELECTはオフのまま");
            Assert.IsFalse(split.TryGetState(groupA.Group, out _), "解消した対象のグループは状態を持たない");
            Assert.AreEqual(0, split.SelectedGroupCount, "整理で処理した対象のSELECTは消費され、選択件数は0");
            Assert.AreSame(cautionResult, groupB.Verification, "確認結果を保つ");
            Assert.AreSame(groupB.Group, library.SplitPlacementGroups!.Single(), "結果のグループのインスタンスも引き継がれる");
            Assert.IsTrue(library.HasCurrentSplitPlacementGroups(applied), "同じ受付の中で適用中の探索条件で探索し直し、有効な結果がある");
            Assert.IsFalse(library.HasCurrentSplitPlacementGroups(SplitPlacementSearchCriteria.Default), "既定値で探索し直していない");
            Assert.AreEqual(Visibility.Visible, table.Visibility);
            Assert.AreEqual(Visibility.Collapsed, main.Visibility, "譜面一覧は出さない");
            Assert.AreEqual(0, viewModel.MainChartList.Rows.Count);
            Assert.IsTrue(RealizeCandidateListNode(splitRoot).IsSelected, "ノードの選択を保つ");

            // 実ファイル・DB・重複結果・案内。
            Assert.IsFalse(Directory.Exists(sourceA), "統合元のフォルダは削除される");
            Assert.IsTrue(File.Exists(Path.Combine(keepA, "chart-0-0.bms")), "統合先の譜面は残る");
            Assert.IsTrue(Directory.Exists(rows[2].Path) && Directory.Exists(rows[3].Path), "グループBのフォルダは変わらない");
            string songDb = Path.Combine(Path.GetDirectoryName(keepA)!, "song.db");
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(library.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), songDb).OpenSongDbReadOnly())
            {
                string[] paths = [.. db.Table<LR2SongDB.song>().ToList().Select(song => song.path)];
                Assert.IsFalse(paths.Any(path => path.StartsWith(sourceA, StringComparison.OrdinalIgnoreCase)), "統合元の譜面の行はDBから除かれる");
            }
            Assert.IsNull(library.DuplicateChartGroups, "重複結果は明示的に準備されず、失効したまま");
            UiMessageRequest result = dialogs.Messages.Last();
            Assert.AreEqual(2, dialogs.Messages.Count, "未確認の案内と結果の案内だけ（Busy案内を含まない）");
            Assert.AreEqual(Resources.Duplicate_batch_organize_title, result.Caption);
            Assert.AreEqual(string.Format(Resources.Duplicate_batch_organize_result_format, 1, 1, 0, 0, 0, 0), result.MessageBoxText.Split(Environment.NewLine)[0]);
            Assert.AreEqual(true, gateFreeAtResult, "結果の案内の時点では受付も活動も解放済み");
            Assert.IsFalse(viewModel.ChartMutationActivity.IsActive);
            Assert.IsTrue(composition.OperationAdmission.TryEnter(out IDisposable? next), "受付を解放する");
            next.Dispose();
        }, dialogService: dialogs, uniqueChartNames: true);
    }

    /// <summary>進捗の要求の参照を、後で同一性を比べるために保持します。</summary>
    private sealed record UiProgressRequestHolder(BeMusicSeeker.Views.Dialogs.UiProgressRequest? Request);
}
