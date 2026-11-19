using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.Tests.TreeSelectionDisplayTestSupport;
using ConversionTestSupport = BeMusicSeeker.TestSupport.Components.SelectedChartAudioConversionWorkflowOwnerTestSupport;

namespace BeMusicSeeker.Tests;

public sealed partial class MainWindowPackageMaintenanceWpfTests
{
    /// <summary>
    /// 保証：重複ツリーの子の先頭に統合候補一覧のノードが現れ、選ぶと専用の表へ切り替わる。表示中は譜面一覧の表・メインの検索欄・メインの件数表示・
    /// 保留譜面の操作バー・プレイログの集計バーを出さず、専用の検索欄を出す。譜面一覧は行0件・選択なし、ツリーの選択表示は一つだけで、
    /// 件数表示は「表示中 n グループ／選択 m グループ」になる。専用の検索欄への入力は束縛を通って一覧を絞り込む。
    /// 表の操作で変えた列幅は一覧の列設定だけに入り、保存される側の列設定（設定の全列設定と、表示中の譜面一覧の列設定）を変えない。
    /// 別の画面へ移ると専用の表・検索欄を隠し、メインの表・検索欄・件数表示を移動先の元の条件で表示し、戻っても状態・並べ替え・検索語（検索欄の文字）・列幅を保つ。
    /// 検出する誤り：ノードの位置・欠落、切り替わらない表示、表示中に残るメインの件数表示・バー、検索欄と一覧の束縛の欠落、
    /// 隠れた譜面一覧の行の残り、選択表示の二重化、別画面へ移った後の一覧・検索欄の残り、列幅の保存側への書込み・共有、再訪での状態・検索欄・列幅の消失。
    /// </summary>
    [TestMethod]
    public void CandidateList_NodeSwitchesToDedicatedTableAndKeepsStateAcrossScreens()
    {
        RunDuplicateSelectionFixture((scope, viewModel, window, library, charts, navigation) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            var root = (TreeViewItem)window.FindName("treeViewItemSearchDuplicated");
            var tree = (TreeView)window.FindName("treeView");
            var main = (CustomTableView)window.FindName("customTableView");
            var table = (CustomTableView)window.FindName("customTableDuplicateCandidates");
            var search = (Border)window.FindName("KeywordSearchComposite");
            var candidateSearch = (Border)window.FindName("duplicateCandidateSearchComposite");
            var candidateEditor = (TextBox)window.FindName("duplicateCandidateSearchEditor");
            var bar = (Border)window.FindName("duplicateCandidateOperationBar");
            var summary = (TextBlock)window.FindName("duplicateCandidateSummaryText");
            var pendingBar = (Border)window.FindName("pendingInstallOperationBar");
            var historyBar = (Border)window.FindName("playHistorySummaryBar");
            TextBlock mainSummary = FindMainSummaryText(window);
            DuplicateMergeCandidateListOwner candidates = viewModel.DuplicateMergeCandidateList;

            TreeViewItem node = RealizeCandidateListNode(root);
            Assert.AreEqual(1 + library.DuplicateChartGroups.Count, root.Items.Count, "ノードとグループ項目");
            Assert.IsInstanceOfType<DuplicateMergeCandidateListNode>(root.Items[0], "ノードは先頭です。");
            Assert.IsFalse(candidates.IsViewActive);
            Assert.AreEqual(Visibility.Collapsed, table.Visibility);
            Assert.AreEqual(Visibility.Collapsed, candidateSearch.Visibility, "専用の検索欄は一覧の表示中だけ出します。");
            Assert.AreEqual(Visibility.Visible, main.Visibility);
            Assert.AreEqual(Visibility.Visible, mainSummary.Visibility);
            Assert.AreEqual(Resources.Duplicate_merge_candidate_list, node.Header);

            SelectCandidateListAndAwaitView(viewModel, navigation, node);

            Assert.AreEqual(Visibility.Visible, table.Visibility);
            Assert.AreEqual(Visibility.Visible, bar.Visibility);
            Assert.AreEqual(Visibility.Visible, candidateSearch.Visibility, "専用の検索欄を表示します。");
            Assert.AreEqual(Visibility.Collapsed, main.Visibility, "譜面一覧の表は表示しません。");
            Assert.AreEqual(Visibility.Collapsed, search.Visibility, "メインの検索欄は表示しません。");
            AssertCandidateListHidesMainBars(mainSummary, pendingBar, historyBar);
            Assert.AreEqual(0, viewModel.MainChartList.Rows.Count, "隠れた譜面一覧は行0件です。");
            Assert.AreEqual(-1, viewModel.MainChartList.SelectedIndex);
            Assert.AreEqual(4, table.ItemsSource.Count);
            Assert.AreEqual(string.Format(Resources.Duplicate_merge_candidate_summary, 2, 0), summary.Text);
            Assert.AreEqual(1, FindDescendants<TreeViewItem>(tree).Count(item => item.IsSelected), "選択表示は一つだけです。");
            CollectionAssert.AreEqual(
                new[] { "SELECT", "STATUS", "KEEP", "PATH", "FOLDER", "TITLE", "ARTIST" }, table.Columns.Select(column => column.Header).ToArray());

            Click(table, 1, "Select");
            Assert.AreEqual(1, candidates.SelectedGroupCount);
            Assert.AreEqual(string.Format(Resources.Duplicate_merge_candidate_summary, 2, 1), summary.Text);
            candidates.RequestSort("Path", ListSortDirection.Descending);
            DuplicateGroup first = library.DuplicateChartGroups[0];
            DuplicateGroup second = library.DuplicateChartGroups[1];

            // 専用の検索欄への入力は、束縛（遅延あり）を通って一覧を絞り込む。選択済みのグループ0は表示外になり、件数の選択数から外れる。
            EnterCandidateSearch(candidates, candidateEditor, "group1");
            Assert.AreEqual(1, candidates.VisibleGroupCount);
            CollectionAssert.AreEqual(new[] { second, second }, table.ItemsSource.Cast<DuplicateMergeCandidateRow>().Select(row => row.State.Group).ToArray(),
                "表は絞り込んだ行集合を表示します。");
            Assert.AreEqual(string.Format(Resources.Duplicate_merge_candidate_summary, 1, 0), summary.Text);

            // 表の見出し境界の操作で列幅を変える。一覧の列設定だけが変わり、保存される側の列設定は変わらない。
            string[] savedBefore = SnapshotSavedColumnLayouts(viewModel);
            const int statusWidth = 210;
            ResizeCandidateColumn(table, "Status", statusWidth);
            Assert.AreEqual(statusWidth, candidates.ColumnsSettings.Status.Width, "一覧の列設定へ反映します。");
            CollectionAssert.AreEqual(savedBefore, SnapshotSavedColumnLayouts(viewModel), "保存される側の列設定は変えません。");

            // 別の画面（重複グループ）へ移る。専用の表・検索欄を隠し、メインの表・検索欄・件数表示を元の条件で表示する。
            TreeViewItem groupItem = RealizeDuplicateGroupItem(root, second);
            SelectDuplicateTreeAndAwaitRows(viewModel, window, navigation, () => groupItem.IsSelected = true,
                second.ChartFiles.Select(chart => chart.Path).ToArray());
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.IsFalse(candidates.IsViewActive);
            Assert.AreEqual(Visibility.Collapsed, table.Visibility);
            Assert.AreEqual(Visibility.Collapsed, bar.Visibility);
            Assert.AreEqual(Visibility.Collapsed, candidateSearch.Visibility, "専用の検索欄を隠します。");
            Assert.AreEqual(Visibility.Visible, main.Visibility);
            Assert.AreEqual(Visibility.Visible, search.Visibility, "メインの検索欄は移動先の元の条件で表示されます。");
            Assert.AreEqual(Visibility.Visible, mainSummary.Visibility, "メインの件数表示は移動先の元の条件で表示されます。");
            Assert.AreEqual(Visibility.Collapsed, pendingBar.Visibility, "重複グループの画面は保留の操作バーを出しません。");
            Assert.AreEqual(Visibility.Collapsed, historyBar.Visibility, "重複グループの画面はプレイログの集計バーを出しません。");

            node = RealizeCandidateListNode(root);
            SelectCandidateListAndAwaitView(viewModel, navigation, node);
            TestUiDispatcherHost.ProcessQueuedPresentation();
            AssertCandidateListHidesMainBars(mainSummary, pendingBar, historyBar);
            Assert.AreEqual("group1", candidateEditor.Text, "再訪で検索欄の文字が戻ります。");
            Assert.AreEqual((1, 0), (candidates.VisibleGroupCount, candidates.SelectedGroupCount), "再訪しても検索の絞込みを保ちます。");
            Assert.AreEqual(("Path", ListSortDirection.Descending, "group1"),
                (candidates.SortColumnName, candidates.SortDirection, candidates.SearchText));
            Assert.AreEqual(statusWidth, candidates.ColumnsSettings.Status.Width, "列幅は起動中だけ保持します。");
            Assert.AreEqual(statusWidth, table.Columns.Single(column => column.Id == "Status").Width, "再訪した表も変更後の列幅を使います。");

            // 検索欄を空にすると、保っていた状態（グループ0のSELECT）と降順の並べ替えが全グループに現れる。
            EnterCandidateSearch(candidates, candidateEditor, string.Empty);
            Assert.AreEqual(2, candidates.VisibleGroupCount);
            Assert.AreEqual(1, candidates.SelectedGroupCount, "別の画面へ移って戻っても状態を保ちます。");
            Assert.IsTrue(candidates.RowSet.Where(row => row.State.Group == first).All(row => row.State.IsSelected));
            Assert.AreSame(first, candidates.RowSet.Select(row => row.State.Group).Last(), "降順の並べ替えを保ちます。");
            Assert.AreEqual(1, FindDescendants<TreeViewItem>(tree).Count(item => item.IsSelected));
        });
    }

    /// <summary>
    /// 保証：統合候補一覧から保留の画面・プレイログの画面へ移ると、一覧と専用の検索欄・ツールバーを隠し、移動先の元の条件でその画面のバーと件数表示を出す。
    /// 保留の画面は保留譜面の操作バーとメインの件数表示を出し、プレイログの画面は集計バーを出してメインの件数表示を出さない。一覧へ戻るとそれらをすべて隠す。
    /// 検出する誤り：一覧の表示が残って移動先のバーを覆う誤り、移動先のバーが出ない誤り、一覧へ戻ったときに前の画面のバー・件数表示が残る誤り。
    /// </summary>
    [TestMethod]
    public void CandidateList_LeavingForPendingOrPlayHistoryShowsThatScreensOwnBars()
    {
        RunDuplicateSelectionFixture((scope, viewModel, window, library, charts, navigation) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            var root = (TreeViewItem)window.FindName("treeViewItemSearchDuplicated");
            var pendingRoot = (TreeViewItem)window.FindName("treeViewItemInstallPending");
            var historyRoot = (TreeViewItem)window.FindName("treeViewItemPlayHistory");
            var table = (CustomTableView)window.FindName("customTableDuplicateCandidates");
            var candidateSearch = (Border)window.FindName("duplicateCandidateSearchComposite");
            var bar = (Border)window.FindName("duplicateCandidateOperationBar");
            var pendingBar = (Border)window.FindName("pendingInstallOperationBar");
            var historyBar = (Border)window.FindName("playHistorySummaryBar");
            TextBlock mainSummary = FindMainSummaryText(window);
            DuplicateMergeCandidateListOwner candidates = viewModel.DuplicateMergeCandidateList;

            SelectCandidateListAndAwaitView(viewModel, navigation, RealizeCandidateListNode(root));
            TestUiDispatcherHost.ProcessQueuedPresentation();
            AssertCandidateListHidesMainBars(mainSummary, pendingBar, historyBar);

            // 保留の画面へ移る。
            SelectDuplicateTreeAndAwaitRows(viewModel, window, navigation, () => pendingRoot.IsSelected = true, [], null,
                MainViewUpdateMode.PendingInstallFolderSelected);
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.IsFalse(candidates.IsViewActive);
            AssertCandidateListHidden(table, candidateSearch, bar);
            Assert.AreEqual(Visibility.Visible, pendingBar.Visibility, "保留の画面は保留譜面の操作バーを出します。");
            Assert.AreEqual(Visibility.Collapsed, historyBar.Visibility);
            Assert.AreEqual(Visibility.Visible, mainSummary.Visibility, "保留の画面はメインの件数表示を出します。");

            SelectCandidateListAndAwaitView(viewModel, navigation, RealizeCandidateListNode(root));
            TestUiDispatcherHost.ProcessQueuedPresentation();
            AssertCandidateListHidesMainBars(mainSummary, pendingBar, historyBar);

            // プレイログの画面へ移る。
            SelectPlayHistoryAndAwaitView(viewModel, historyRoot);
            Assert.IsFalse(candidates.IsViewActive);
            AssertCandidateListHidden(table, candidateSearch, bar);
            Assert.AreEqual(Visibility.Visible, historyBar.Visibility, "プレイログの画面は集計バーを出します。");
            Assert.AreEqual(Visibility.Collapsed, pendingBar.Visibility);
            Assert.AreEqual(Visibility.Collapsed, mainSummary.Visibility, "プレイログの画面はメインの件数表示を出しません。");

            SelectCandidateListAndAwaitView(viewModel, navigation, RealizeCandidateListNode(root));
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.IsFalse(viewModel.PlayHistory.IsViewActive);
            AssertCandidateListHidesMainBars(mainSummary, pendingBar, historyBar);
            Assert.AreEqual(Visibility.Visible, table.Visibility);
            Assert.AreEqual(Visibility.Visible, candidateSearch.Visibility);
            Assert.AreEqual(Visibility.Visible, bar.Visibility);
        });
    }

    /// <summary>
    /// 保証：SELECT・KEEPのセルのクリックは、結合列の先頭でない行でもそのグループの値を切り替え（有効なSELECT・KEEPのダブルクリックは遷移を起こさない）、
    /// Spaceキー・右クリックメニュー・ツールバーが選択行・表示中のグループへ作用する。「エクスプローラーで開く」は存在する選択フォルダごとに一度だけ開く。
    /// 検出する誤り：先頭行でしか効かないクリック、ダブルクリックでの遷移、複数行選択でのグループごとの重複作用、非表示グループの巻込み、存在しないフォルダを開く誤り。
    /// </summary>
    [TestMethod]
    public void CandidateList_CellsKeyMenuAndToolbarActOnGroupsAndExplorerOpensExistingFoldersOnce()
    {
        var gateway = new RecordingCandidateShellGateway();
        RunDuplicateSelectionFixture((scope, viewModel, window, library, charts, navigation) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            var root = (TreeViewItem)window.FindName("treeViewItemSearchDuplicated");
            var table = (CustomTableView)window.FindName("customTableDuplicateCandidates");
            DuplicateMergeCandidateListOwner candidates = viewModel.DuplicateMergeCandidateList;
            SelectCandidateListAndAwaitView(viewModel, navigation, RealizeCandidateListNode(root));
            DuplicateMergeCandidateRow[] rows = [.. candidates.RowSet];
            Assert.AreEqual(4, rows.Length);
            int requestsBefore = navigation.Count;

            // クリック：先頭でない行（行1）のSELECT・KEEPでも、そのグループの値を切り替える。
            Click(table, 1, "Select");
            Assert.IsTrue(rows[0].State.IsSelected && rows[1].State.IsSelected && !rows[2].State.IsSelected);
            Click(table, 1, "Select", clickCount: 2);
            Assert.IsTrue(rows[0].State.IsSelected, "ダブルクリックは操作として二回数え、元の状態へ戻ります。");
            Assert.AreEqual(requestsBefore, navigation.Count, "有効なSELECTのダブルクリックは遷移を起こしません。");
            Click(table, 1, "Select");
            Assert.IsFalse(rows[0].State.IsSelected);
            Click(table, 1, "Keep");
            Assert.AreEqual(rows[1].Path, rows[0].State.KeepFolder);
            Assert.IsFalse(rows[0].IsKeep);
            Click(table, 1, "Keep", clickCount: 2);
            Assert.AreEqual(requestsBefore, navigation.Count, "有効なKEEPのダブルクリックは遷移を起こしません。");

            // Space：選択した二つのグループのSELECTを、グループごとに一度だけ切り替える。
            table.SelectRowsByPredicate(row => row is DuplicateMergeCandidateRow { Index: 0 or 1 or 2 });
            RaiseSpace(table);
            Assert.IsTrue(rows.All(row => row.State.IsSelected), "すべてオフならすべてオン");
            RaiseSpace(table);
            Assert.IsTrue(rows.All(row => !row.State.IsSelected), "すべてオンならすべてオフ");

            // メニュー。
            var menu = (ContextMenu)window.FindResource("duplicateCandidateContextMenu");
            // 項目は見出しのリソースで特定する（並びは固定しない）。
            MenuItem MenuItemOf(string header) => menu.Items.OfType<MenuItem>().Single(item => (string)item.Header == header);
            MenuItem checkOn = MenuItemOf(Resources.Duplicate_merge_candidate_check_on);
            MenuItem checkOff = MenuItemOf(Resources.Duplicate_merge_candidate_check_off);
            MenuItem openExplorer = MenuItemOf(Resources.Open_file_explorer);
            Assert.IsNotNull(MenuItemOf(Resources.Duplicate_merge_candidate_verify_group), "「このグループを確認」の項目があります。");
            table.SelectRowsByPredicate(row => row is DuplicateMergeCandidateRow { Index: 0 or 1 });
            RaiseMenuClick(checkOn);
            Assert.IsTrue(rows[0].State.IsSelected && !rows[2].State.IsSelected);
            RaiseMenuClick(checkOff);
            Assert.IsFalse(rows[0].State.IsSelected);

            // エクスプローラー：存在するフォルダごとに一度。
            Directory.Delete(rows[3].Path, recursive: true);
            table.SelectRowsByPredicate(row => true);
            RaiseMenuClick(openExplorer);
            CollectionAssert.AreEquivalent(new[] { rows[0].Path, rows[1].Path, rows[2].Path }, gateway.OpenedDirectoryPaths.ToArray(),
                "存在するフォルダだけを、一度ずつ開きます。");

            // ツールバー：検索で表示中のグループだけに作用する。
            candidates.SearchText = Path.GetFileName(rows[2].Path);
            Assert.AreEqual(1, candidates.VisibleGroupCount);
            ClickButton(window, "duplicateCandidateSelectVisibleButton");
            Assert.IsTrue(rows[2].State.IsSelected && !rows[0].State.IsSelected);
            candidates.SearchText = string.Empty;
            Assert.AreEqual(1, candidates.SelectedGroupCount, "非表示だったグループの選択は変わりません。");
            ClickButton(window, "duplicateCandidateClearVisibleButton");
            Assert.AreEqual(0, candidates.SelectedGroupCount);
        }, externalShellGateway: gateway);
    }

    /// <summary>
    /// 保証：行のダブルクリック（無効なセルを含む有効なSELECT・KEEP以外のセル）は、重複ツリーの対応するグループを展開し、そのフォルダノードを選択して、既存のフォルダ表示へ移る。
    /// 画面外（仮想化で未生成）のグループでも同じで、現在の結果でフォルダを解決できなければ何もしない。
    /// 検出する誤り：未生成のグループで何も起きない、グループ全体を選ぶ誤り、フォルダの取り違え、解決できない行での例外。
    /// </summary>
    [TestMethod]
    public void CandidateList_DoubleClickSelectsFolderNodeInTreeEvenForUnrealizedGroups()
    {
        RunDuplicateSelectionFixture((scope, viewModel, window, library, charts, navigation) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            var root = (TreeViewItem)window.FindName("treeViewItemSearchDuplicated");
            var table = (CustomTableView)window.FindName("customTableDuplicateCandidates");
            DuplicateMergeCandidateListOwner candidates = viewModel.DuplicateMergeCandidateList;
            SelectCandidateListAndAwaitView(viewModel, navigation, RealizeCandidateListNode(root));
            DuplicateMergeCandidateRow target = candidates.RowSet[^1];
            DuplicateGroup lastGroup = library.DuplicateChartGroups[^1];
            Assert.IsNull(root.ItemContainerGenerator.ContainerFromItem(lastGroup), "前提：最後のグループは仮想化で未生成です。");
            string[] expectedPaths = [.. lastGroup.ChartFiles.Where(chart => string.Equals(chart.Directory, target.Path, StringComparison.OrdinalIgnoreCase)).Select(chart => chart.Path)];
            Assert.IsTrue(expectedPaths.Length > 0);

            DoubleClickAndAwaitFolderRows(viewModel, window, navigation, table, target.Index, "Path", expectedPaths);

            Assert.IsFalse(candidates.IsViewActive, "既存のフォルダ表示へ移り、専用の表を隠します。");
            Assert.AreEqual(Visibility.Visible, ((CustomTableView)window.FindName("customTableView")).Visibility);

            // 解決できない行（現在の結果にないグループ、現在のグループにないフォルダ）では何もしない。
            var orphanGroup = new DuplicateGroup([], [@"C:\Nowhere\a", @"C:\Nowhere\b"]);
            var orphanState = new DuplicateMergeCandidateGroupState(new DuplicateMergeCandidateGroupInitial(
                orphanGroup, orphanGroup.Folders, orphanGroup.Folders[0], false, false, []));
            DuplicateMergeCandidateRow vanishedGroup = new(orphanState, @"C:\Nowhere\a", "a", string.Empty, string.Empty, 0, 0);
            DuplicateMergeCandidateRow vanishedFolder = new(candidates.RowSet[0].State, @"C:\Nowhere\other", "other", string.Empty, string.Empty, 0, 0);
            int requests = navigation.Count;
            InvokeRowActivated(window, table, vanishedGroup);
            InvokeRowActivated(window, table, vanishedFolder);
            Assert.AreEqual(requests, navigation.Count, "解決できなければ何もしません。");
        }, fillerGroups: 120);
    }

    /// <summary>
    /// 保証：「優先統合先…」はウィンドウ（既存の所有付き表示の窓口）を開き、OKで確定した登録内容を一覧の全グループへ適用し、グループ内でちょうど一つのフォルダが優先先の配下に当たればそのフォルダをKEEPにする。
    /// 開き直すと前回の登録内容が出て、キャンセルは登録内容もKEEPも変えない。ウィンドウ内の「追加」はフォルダ選択（記録用の窓口）の結果を登録し、「削除」は選択した登録を除く。
    /// 検出する誤り：キャンセルでの変更、開き直しで内容が消える、窓口を通さない表示、追加・削除の取り違え。
    /// </summary>
    [TestMethod]
    public void CandidateList_PreferredDestinationsWindowAppliesOnAcceptAndKeepsRegistration()
    {
        var dialogs = new RecordingPreferredDestinationDialogs();
        RunDuplicateSelectionFixture((scope, viewModel, window, library, charts, navigation) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            var root = (TreeViewItem)window.FindName("treeViewItemSearchDuplicated");
            DuplicateMergeCandidateListOwner candidates = viewModel.DuplicateMergeCandidateList;
            SelectCandidateListAndAwaitView(viewModel, navigation, RealizeCandidateListNode(root));
            DuplicateMergeCandidateRow[] rows = [.. candidates.RowSet];
            string preferred = rows[1].Path;
            Assert.AreEqual(rows[0].Path, rows[0].State.KeepFolder, "前提：既定のKEEPはPATH昇順の先頭です。");

            dialogs.NextResult = new UiWindowDialogResult<IReadOnlyList<string>>(UiDialogStatus.CancelledByUser);
            ClickButton(window, "duplicateCandidatePreferredDestinationsButton");
            Assert.AreEqual(1, dialogs.Opened.Count);
            CollectionAssert.AreEqual(Array.Empty<string>(), dialogs.Opened[0].ToArray(), "初回は空の登録内容です。");
            Assert.AreEqual(rows[0].Path, rows[0].State.KeepFolder, "キャンセルはKEEPを変えません。");
            Assert.AreEqual(0, candidates.PreferredDestinations.Count);

            dialogs.NextResult = new UiWindowDialogResult<IReadOnlyList<string>>(UiDialogStatus.Accepted, [preferred], true);
            ClickButton(window, "duplicateCandidatePreferredDestinationsButton");
            Assert.AreEqual(preferred, rows[0].State.KeepFolder, "グループ内でちょうど一つのフォルダが優先先に当たればKEEPにします。");
            CollectionAssert.AreEqual(new[] { preferred }, candidates.PreferredDestinations.ToArray());

            dialogs.NextResult = new UiWindowDialogResult<IReadOnlyList<string>>(UiDialogStatus.CancelledByUser);
            ClickButton(window, "duplicateCandidatePreferredDestinationsButton");
            CollectionAssert.AreEqual(new[] { preferred }, dialogs.Opened[2].ToArray(), "開き直すと前回の内容が出ます。");
            CollectionAssert.AreEqual(new[] { preferred }, candidates.PreferredDestinations.ToArray(), "キャンセルは登録内容を変えません。");

            // ウィンドウ内の追加・削除（フォルダ選択は記録用の窓口）。
            var input = new DuplicatePreferredDestinationDialogViewModel([@"C:\Existing"]);
            var dialog = new DuplicatePreferredDestinationDialog(input, dialogs);
            dialogs.NextFolders = [@"C:\Picked", @"c:\existing\"];
            ClickDialogButton(dialog, "addButton");
            CollectionAssert.AreEqual(new[] { @"C:\Existing", @"C:\Picked" }, input.Destinations.ToArray(), "重複した登録は一つにまとめます。");
            dialog.Measure(new Size(640d, 420d));
            dialog.Arrange(new Rect(0d, 0d, 640d, 420d));
            dialog.UpdateLayout();
            var list = (ListBox)dialog.FindName("destinationList");
            list.SelectedItems.Add(@"C:\Existing");
            ClickDialogButton(dialog, "removeButton");
            CollectionAssert.AreEqual(new[] { @"C:\Picked" }, input.Destinations.ToArray());
        }, dialogService: dialogs);
    }

    /// <summary>
    /// 保証：表示言語を変えると、ノードの見出し・STATUS・件数・ボタン・メニューが現在の言語に追従する。終わったら元の言語へ戻す。
    /// 検出する誤り：起動時の言語のまま残る文言、言語の変更で一覧の状態（SELECT）が失われる誤り。
    /// </summary>
    [TestMethod]
    public void CandidateList_PresentationFollowsDisplayLanguageRoundTrip()
    {
        RunDuplicateSelectionFixture((scope, viewModel, window, library, charts, navigation) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            var root = (TreeViewItem)window.FindName("treeViewItemSearchDuplicated");
            var table = (CustomTableView)window.FindName("customTableDuplicateCandidates");
            var summary = (TextBlock)window.FindName("duplicateCandidateSummaryText");
            var select = (Button)window.FindName("duplicateCandidateSelectVisibleButton");
            var verify = (Button)window.FindName("duplicateCandidateVerifyButton");
            var quickCompare = (CheckBox)window.FindName("duplicateCandidateQuickCompareCheckBox");
            var menu = (ContextMenu)window.FindResource("duplicateCandidateContextMenu");
            DuplicateMergeCandidateListOwner candidates = viewModel.DuplicateMergeCandidateList;
            TreeViewItem node = RealizeCandidateListNode(root);
            SelectCandidateListAndAwaitView(viewModel, navigation, node);
            // 確認済みのグループ（要注意、理由に該当の一覧あり）を書き込み、STATUSとツールチップの追従も確かめる。
            DuplicateMergeVerificationResult cautionResult = new(
                DuplicateMergeVerificationStage.Caution,
                [new DuplicateMergeVerificationReason(DuplicateMergeVerificationReasonKind.ParentReference, [new DuplicateMergeFinding(candidates.RowSet[0].Path, "c.bms", "../x.wav")])]);
            candidates.WriteVerifications([new DuplicateMergeVerificationWrite(candidates.RowSet[0].State.Group, cautionResult)]);
            Click(table, 0, "Select");
            string previous = Resources.Culture?.Name ?? "ja-JP";
            string other = previous == "en-US" ? "ja-JP" : "en-US";
            try
            {
                ResourceService.Current.ChangeCulture(other);
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.AreEqual(Resources.Duplicate_merge_candidate_list, node.Header);
                Assert.AreEqual(string.Format(Resources.Duplicate_merge_candidate_summary, 2, 1), summary.Text);
                Assert.AreEqual(Resources.Duplicate_merge_candidate_select_visible, select.Content);
                Assert.AreEqual(Resources.Duplicate_merge_candidate_check_on, menu.Items.OfType<MenuItem>().First().Header);
                Assert.AreEqual(Resources.Duplicate_merge_candidate_verify_selected, verify.Content);
                Assert.AreEqual(Resources.Duplicate_merge_candidate_quick_compare, quickCompare.Content);
                Assert.IsTrue(menu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, Resources.Duplicate_merge_candidate_verify_group)));
                Assert.AreEqual(
                    string.Format(Resources.Duplicate_merge_candidate_status_caution_format, Resources.Duplicate_merge_candidate_reason_parent_reference),
                    candidates.RowSet[0].StatusText, "確認済みのSTATUSは現在の言語で作り直します。");
                string tooltip = ResolveStatusTooltip(table, 0);
                StringAssert.StartsWith(tooltip, Resources.Duplicate_merge_candidate_reason_parent_reference_description, "ツールチップも現在の言語です。");
                Assert.AreEqual(Resources.Duplicate_merge_candidate_status_unverified, candidates.RowSet[2].StatusText, "未確認のグループは未確認のままです。");
                Assert.AreEqual(1, candidates.SelectedGroupCount, "言語の変更で状態は失われません。");
            }
            finally { ResourceService.Current.ChangeCulture(previous); }
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.AreEqual(string.Format(Resources.Duplicate_merge_candidate_summary, 2, 1), summary.Text);
        });
    }

    /// <summary>STATUS列のセルに出る説明（ツールチップ）を、実のヒット判定から取得します。</summary>
    private static string ResolveStatusTooltip(CustomTableView table, int rowIndex)
    {
        CustomTableHitTestResult hit = HitCell(table, rowIndex, "Status", out _);
        return table.ResolveCellToolTip(hit) ?? string.Empty;
    }

    /// <summary>
    /// 保証：実画面の「選択グループを確認」ボタンと右クリックメニューの「このグループを確認」が、実ファイルの確認を通じてSTATUS・SELECT・件数表示・ツールチップを更新し、
    /// 進捗の要求はメインウィンドウを所有者として出して、結果の案内を一件だけ出す。簡易確認のチェックは、オンにしたときだけ案内を一件出して管理主体へ値を渡す。
    /// 実ファイルの衝突（別内容の資源）のグループは要注意でSELECTがオフになり、衝突を解消して右クリックで再確認すれば安全でオンに戻る。
    /// 標準構成の接続は、共通の受付が使用中なら確認がBusyの案内で未開始になることで確かめる。条件の網羅は検査器・管理主体のケースが担当する。
    /// 検出する誤り：ボタン・メニュー・チェックの未接続、進捗の所有者の欠落、案内の重複・欠落、SELECT・件数表示・STATUSの更新漏れ、標準構成の受付への未接続。
    /// </summary>
    [TestMethod]
    public void CandidateList_VerifyButtonMenuAndQuickCompareCheckActThroughRealFiles()
    {
        var events = new ConversionTestSupport.EventLog();
        var dialogs = new ConversionTestSupport.RecordingDialogService(events);
        RunDuplicateSelectionFixture((scope, viewModel, window, library, charts, navigation) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            var root = (TreeViewItem)window.FindName("treeViewItemSearchDuplicated");
            var table = (CustomTableView)window.FindName("customTableDuplicateCandidates");
            var summary = (TextBlock)window.FindName("duplicateCandidateSummaryText");
            var quickCompare = (CheckBox)window.FindName("duplicateCandidateQuickCompareCheckBox");
            var menu = (ContextMenu)window.FindResource("duplicateCandidateContextMenu");
            DuplicateMergeCandidateListOwner candidates = viewModel.DuplicateMergeCandidateList;
            SelectCandidateListAndAwaitView(viewModel, navigation, RealizeCandidateListNode(root));
            DuplicateMergeCandidateRow[] rows = [.. candidates.RowSet];
            Assert.AreEqual(4, rows.Length);
            // グループ1（行2・3）のフォルダに、同じ相対パスで内容が異なる資源を置く。グループ0は同一内容の譜面だけで衝突しない。
            File.WriteAllBytes(Path.Combine(rows[2].Path, "x.bin"), [1, 2, 3, 4]);
            File.WriteAllBytes(Path.Combine(rows[3].Path, "x.bin"), [5, 6, 7, 8]);
            // 同一内容の資源を共有させ、連結の不足（同一内容ファイルなし）を別の理由として混ぜない。
            File.WriteAllBytes(Path.Combine(rows[2].Path, "shared.bin"), [9, 9, 9]);
            File.WriteAllBytes(Path.Combine(rows[3].Path, "shared.bin"), [9, 9, 9]);
            Click(table, 0, "Select");
            Click(table, 2, "Select");
            Assert.AreEqual(2, candidates.SelectedGroupCount);
            int messagesBefore = dialogs.Messages.Count;

            // 標準構成の受付が使用中なら、確認はBusyの案内で未開始になる。
            Assert.IsTrue(MainWindowViewModelTestFactory.GetComposition(viewModel).OperationAdmission.TryEnter(out IDisposable? lease));
            try
            {
                AwaitHandler(() => ClickButton(window, "duplicateCandidateVerifyButton"), "verify-busy");
                Assert.AreEqual(messagesBefore + 1, dialogs.Messages.Count);
                Assert.AreEqual(Resources.Warn_LibraryOperationBusy, dialogs.Messages[^1].MessageBoxText);
                Assert.IsNull(dialogs.ProgressRequest, "未開始のため進捗を出しません。");
                Assert.AreEqual(Resources.Duplicate_merge_candidate_status_unverified, rows[0].StatusText);
            }
            finally { lease?.Dispose(); }

            // ツールバー：実際の確認。
            messagesBefore = dialogs.Messages.Count;
            AwaitHandler(() => ClickButton(window, "duplicateCandidateVerifyButton"), "verify-toolbar");
            Assert.AreEqual(Resources.Duplicate_merge_candidate_status_safe, rows[0].StatusText);
            Assert.AreEqual(
                string.Format(Resources.Duplicate_merge_candidate_status_caution_format, Resources.Duplicate_merge_candidate_reason_content_conflict), rows[2].StatusText);
            Assert.IsTrue(rows[0].State.IsSelected, "安全のSELECTはオンです。");
            Assert.IsFalse(rows[2].State.IsSelected, "要注意のSELECTはオフです。");
            Assert.AreEqual(string.Format(Resources.Duplicate_merge_candidate_summary, 2, 1), summary.Text);
            Assert.AreEqual(Resources.Duplicate_merge_candidate_verify_progress_title, dialogs.ProgressRequest!.Title);
            Assert.AreSame(window, dialogs.ProgressRequest.Owner, "進捗の所有者はメインウィンドウです。");
            Assert.AreEqual(messagesBefore + 1, dialogs.Messages.Count, "結果の案内は一件です。");
            Assert.AreEqual(string.Format(Resources.Duplicate_merge_candidate_verify_result_format, 2, 1, 1, 0), dialogs.Messages[^1].MessageBoxText);
            string tooltip = ResolveStatusTooltip(table, 2);
            StringAssert.Contains(tooltip, Resources.Duplicate_merge_candidate_reason_content_conflict_description);
            StringAssert.Contains(tooltip, Path.Combine(rows[2].Path, "x.bin"));
            StringAssert.Contains(ResolveStatusTooltip(table, 0), Resources.Duplicate_merge_candidate_safe_not_compared);

            // 右クリックメニュー：衝突を解消してグループ1だけを再確認する。SELECTの状態によらず対象になり、安全ならオンになる。
            File.WriteAllBytes(Path.Combine(rows[3].Path, "x.bin"), [1, 2, 3, 4]);
            table.SelectRowsByPredicate(row => row is DuplicateMergeCandidateRow { Index: 2 });
            MenuItem verifyGroup = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, Resources.Duplicate_merge_candidate_verify_group));
            messagesBefore = dialogs.Messages.Count;
            AwaitHandler(() => RaiseMenuClick(verifyGroup), "verify-menu");
            Assert.AreEqual(Resources.Duplicate_merge_candidate_status_safe, rows[2].StatusText);
            Assert.IsTrue(rows[2].State.IsSelected, "再確認で安全になればオンになります。");
            Assert.AreEqual(messagesBefore + 1, dialogs.Messages.Count);
            StringAssert.Contains(ResolveStatusTooltip(table, 2), string.Format(Resources.Duplicate_merge_candidate_safe_compared_single_format, 2, Resources.Duplicate_merge_candidate_method_full));

            // 簡易確認のチェック：オンにしたときだけ案内を一件出し、管理主体の値を変える。
            messagesBefore = dialogs.Messages.Count;
            quickCompare.IsChecked = true;
            AwaitHandler(() => quickCompare.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)), "quick-compare-on");
            Assert.IsTrue(candidates.QuickCompareEnabled);
            Assert.AreEqual(messagesBefore + 1, dialogs.Messages.Count);
            Assert.AreEqual(Resources.Duplicate_merge_candidate_quick_compare_notice, dialogs.Messages[^1].MessageBoxText);
            quickCompare.IsChecked = false;
            AwaitHandler(() => quickCompare.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)), "quick-compare-off");
            Assert.IsFalse(candidates.QuickCompareEnabled);
            Assert.AreEqual(messagesBefore + 1, dialogs.Messages.Count, "オフにするときは案内しません。");
        }, dialogService: dialogs);
    }

    /// <summary>実Dispatcherへ配送するasync voidのハンドラーを起こし、その終端まで待ちます。</summary>
    private static void AwaitHandler(Action raise, string name)
        => TestUiDispatcherHost.AwaitTaskOnDispatcher(MainWindowEventCompletion.CaptureAsyncEventCompletion(raise), name);

    private static TreeViewItem RealizeCandidateListNode(TreeViewItem root)
    {
        root.IsExpanded = true;
        root.ApplyTemplate();
        root.Measure(new Size(900d, 700d));
        root.Arrange(new Rect(0d, 0d, 900d, 700d));
        root.UpdateLayout();
        var node = root.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem;
        Assert.IsNotNull(node);
        Assert.IsInstanceOfType<DuplicateMergeCandidateListNode>(node.DataContext);
        return node;
    }

    /// <summary>メインの件数表示（譜面一覧の <c>SummaryText</c> を束縛した文字）を、実画面の要素から探します。名前を持たないため束縛の経路で特定します。</summary>
    private static TextBlock FindMainSummaryText(MainWindow window)
    {
        TextBlock[] found = [.. FindDescendants<TextBlock>((DependencyObject)window.FindName("mainTableToolbar"))
            .Where(text => BindingOperations.GetBindingExpression(text, TextBlock.TextProperty)?.ParentBinding.Path?.Path == "MainChartList.SummaryText")];
        Assert.AreEqual(1, found.Length, "メインの件数表示は一つです。");
        return found[0];
    }

    /// <summary>統合候補一覧の表示中に、メインの件数表示・保留譜面の操作バー・プレイログの集計バーが出ていないことを確かめます。</summary>
    private static void AssertCandidateListHidesMainBars(TextBlock mainSummary, Border pendingBar, Border historyBar)
    {
        Assert.AreEqual(Visibility.Collapsed, mainSummary.Visibility, "一覧の表示中はメインの件数表示を出しません。");
        Assert.AreEqual(Visibility.Collapsed, pendingBar.Visibility, "一覧の表示中は保留譜面の操作バーを出しません。");
        Assert.AreEqual(Visibility.Collapsed, historyBar.Visibility, "一覧の表示中はプレイログの集計バーを出しません。");
    }

    private static void AssertCandidateListHidden(CustomTableView table, Border candidateSearch, Border bar)
    {
        Assert.AreEqual(Visibility.Collapsed, table.Visibility, "一覧の表を隠します。");
        Assert.AreEqual(Visibility.Collapsed, candidateSearch.Visibility, "専用の検索欄を隠します。");
        Assert.AreEqual(Visibility.Collapsed, bar.Visibility, "一覧のツールバーを隠します。");
    }

    /// <summary>
    /// 専用の検索欄へ文字を入れ、束縛（遅延あり）が一覧の検索語へ届くまで待ちます。固定時間は待たず、検索語の変更通知を待ちます。
    /// 検索語の設定は行集合の作り直しまで同期で行うため、通知の後は絞り込んだ行集合を読めます。
    /// </summary>
    private static void EnterCandidateSearch(DuplicateMergeCandidateListOwner owner, TextBox editor, string text)
    {
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler changed = (_, args) =>
        {
            if (args.PropertyName == nameof(DuplicateMergeCandidateListOwner.SearchText) && owner.SearchText == text) { applied.TrySetResult(); }
        };
        owner.PropertyChanged += changed;
        try
        {
            Assert.AreNotEqual(text, owner.SearchText, "前提：入力前の検索語は異なります。");
            editor.Text = text;
            TestUiDispatcherHost.AwaitPresentationOnDispatcher(applied.Task, "candidate search binding");
        }
        finally { owner.PropertyChanged -= changed; }
        TestUiDispatcherHost.ProcessQueuedPresentation();
    }

    /// <summary>表の見出しの右境界を実のヒット判定で捕まえ、幅変更の入力（開始・移動・終了）で列幅を変えます。</summary>
    private static void ResizeCandidateColumn(CustomTableView table, string columnId, int width)
    {
        table.UpdateLayout();
        double edge = -table.HorizontalOffset;
        CustomTableColumn? target = null;
        foreach (CustomTableColumn column in table.Columns)
        {
            edge += column.Width;
            if (column.Id == columnId) { target = column; break; }
        }
        Assert.IsNotNull(target);
        int startWidth = target.Width;
        Assert.AreNotEqual(width, startWidth, "前提：変更前の幅は異なります。");
        CustomTableHitTestResult resize = table.HitTestTable(new Point(edge, table.HeaderHeight / 2d));
        Assert.AreEqual(CustomTableHitKind.HeaderResize, resize.Kind);
        Assert.AreSame(target, resize.Column);
        try
        {
            table.BeginColumnResize(resize, edge);
            // 開始で入力を捕捉すると、現在のマウス位置の移動が同期的に処理され得るため、開始前の幅から求める。
            table.UpdateColumnResize(edge + width - startWidth);
        }
        finally { table.EndColumnResize(); }
        Assert.AreEqual(width, target.Width, "列幅");
    }

    /// <summary>
    /// 保存される側の列設定（設定の全列設定と、表示中の譜面一覧が使う列設定）の幅・表示位置・表示状態を、比較できる文字列の列にします。
    /// 列設定の型を持つ設定の項目は反射で集め、将来の項目の追加でも比較から漏れないようにします。
    /// </summary>
    private static string[] SnapshotSavedColumnLayouts(MainWindowViewModel viewModel)
    {
        Settings settings = MainWindowViewModelTestFactory.GetComposition(viewModel).SettingsEditSession.Values;
        var entries = new List<string>();
        foreach (System.Reflection.PropertyInfo property in typeof(Settings).GetProperties()
            .Where(property => property.PropertyType == typeof(CustomTableColumnSettings)).OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            var columns = (CustomTableColumnSettings?)property.GetValue(settings);
            entries.Add(property.Name + (columns == null ? " null" : " set"));
            entries.AddRange(CustomTableColumnFactory.EnumerateMainColumnLayouts(columns).Select((layout, index) => Describe(property.Name, index, layout)));
        }
        PlaylistSummaryColumnSettings? summary = settings.PlaylistSummaryColumnsSettings;
        entries.Add(nameof(Settings.PlaylistSummaryColumnsSettings) + (summary == null ? " null" : " set"));
        entries.AddRange(CustomTableColumnFactory.EnumeratePlaylistSummaryColumnLayouts(summary)
            .Select((layout, index) => Describe(nameof(Settings.PlaylistSummaryColumnsSettings), index, layout)));
        entries.AddRange(CustomTableColumnFactory.EnumerateMainColumnLayouts(viewModel.MainChartList.ColumnsSettings)
            .Select((layout, index) => Describe("MainChartList", index, layout)));
        Assert.IsTrue(entries.Count > 0);
        return [.. entries];

        static string Describe(string owner, int index, ICustomTableColumnLayout layout)
            => owner + "[" + index + "] " + layout.Width + "/" + layout.DisplayIndex + "/" + layout.Visibility;
    }

    /// <summary>
    /// プレイログのルートを実選択し、開始した履歴の読込みと更新の待ち行列が終わるまで待ちます。読込みのTaskは開始通知で捕捉して回収し、テストの終了後へ残しません。
    /// この環境には履歴の記録がなく読込みは失敗の診断で終わるため、一覧の行ではなく、プレイログの画面が有効になったこと（集計バーの表示条件）を確かめます。
    /// </summary>
    private static void SelectPlayHistoryAndAwaitView(MainWindowViewModel viewModel, TreeViewItem historyRoot)
    {
        var refreshes = new List<Task>();
        Action<long, long, Task> started = (_, _, task) => refreshes.Add(task);
        viewModel.PlayHistory.ViewRefreshStarted += started;
        try
        {
            historyRoot.IsSelected = true;
        }
        finally
        {
            viewModel.PlayHistory.ViewRefreshStarted -= started;
            TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(refreshes), "play history refresh terminals");
            TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.PlayHistory.WaitForRefreshQueuesIdleAsync(), "play history refresh queues idle");
        }
        Assert.IsTrue(refreshes.Count > 0, "選択から履歴の読込みが開始されます。");
        TestUiDispatcherHost.ProcessQueuedPresentation();
        Assert.IsTrue(viewModel.PlayHistory.IsViewActive, "プレイログの画面が有効です。");
    }

    /// <summary>実画面の選択入力から開始した画面受付を回収し、統合候補一覧の採用（表示中への遷移）まで待ちます。</summary>
    private static void SelectCandidateListAndAwaitView(
        MainWindowViewModel viewModel, DuplicateSelectionNavigationTasks navigation, TreeViewItem node)
    {
        DuplicateMergeCandidateListOwner owner = viewModel.DuplicateMergeCandidateList;
        var active = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler changed = (_, args) =>
        {
            if (args.PropertyName == nameof(DuplicateMergeCandidateListOwner.IsViewActive) && owner.IsViewActive) { active.TrySetResult(); }
        };
        owner.PropertyChanged += changed;
        int first = navigation.Count;
        try
        {
            Task handler = MainWindowEventCompletion.CaptureAsyncEventCompletion(() => node.IsSelected = true);
            TestUiDispatcherHost.AwaitTaskOnDispatcher(handler, "candidate list select handler");
            Task<bool>[] requests = navigation.Since(first);
            Assert.IsTrue(requests.Length > 0, "選択から画面受付が配送されます。");
            TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(requests), "candidate list navigation terminals");
            Assert.IsTrue(requests.All(task => task.Result));
            if (owner.IsViewActive) { active.TrySetResult(); }
            TestUiDispatcherHost.AwaitPresentationOnDispatcher(active.Task, "candidate list adoption");
        }
        finally { owner.PropertyChanged -= changed; }
    }

    /// <summary>専用の表の指定行・列のセルを、実の入力（ヒット判定とマウス押下）でクリックします。</summary>
    private static void Click(CustomTableView table, int rowIndex, string columnId, int clickCount = 1)
    {
        CustomTableHitTestResult hit = HitCell(table, rowIndex, columnId, out Point point);
        for (int count = 1; count <= clickCount; count++)
        {
            Assert.IsTrue(table.HandleCellMouseDown(hit, MouseButton.Left, ModifierKeys.None, count, point));
        }
    }

    private static CustomTableHitTestResult HitCell(CustomTableView table, int rowIndex, string columnId, out Point point)
    {
        table.UpdateLayout();
        double x = 0d;
        foreach (CustomTableColumn column in table.Columns)
        {
            if (column.Id == columnId) { x += column.Width / 2d; break; }
            x += column.Width;
        }
        point = new Point(x, table.HeaderHeight + (rowIndex - table.FirstVisibleRowIndex + 0.5d) * table.RowHeight);
        CustomTableHitTestResult hit = table.HitTestTable(point);
        Assert.AreEqual(CustomTableHitKind.Cell, hit.Kind, "ヒット判定が行 " + rowIndex + " を指します。");
        Assert.AreEqual(rowIndex, hit.RowIndex);
        Assert.AreEqual(columnId, hit.Column.Id);
        return hit;
    }

    private static void RaiseSpace(CustomTableView table)
    {
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(table), 0, Key.Space)
        { RoutedEvent = UIElement.KeyDownEvent, Source = table };
        table.RaiseEvent(key);
        Assert.IsTrue(key.Handled);
    }

    private static void ClickButton(MainWindow window, string name)
        => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void ClickDialogButton(Window dialog, string name)
        => ((Button)dialog.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>
    /// 専用の表の行をダブルクリックし、重複ツリーのフォルダノードの選択から開始した画面受付を回収して、
    /// 譜面一覧が期待のパス集合を表示するまで待ちます。表示するパスが同じ別画面の取り違えは、専用の表を隠したことで区別します。
    /// </summary>
    private static void DoubleClickAndAwaitFolderRows(MainWindowViewModel viewModel, MainWindow window,
        DuplicateSelectionNavigationTasks navigation, CustomTableView table, int rowIndex, string columnId, string[] expectedPaths)
    {
        var main = (CustomTableView)window.FindName("customTableView");
        table.SelectedIndex = rowIndex;
        table.ScrollSelectedRowIntoView();
        CustomTableHitTestResult hit = HitCell(table, rowIndex, columnId, out Point point);
        int first = navigation.Count;
        Task handler = MainWindowEventCompletion.CaptureAsyncEventCompletion(() =>
        {
            Assert.IsTrue(table.HandleCellMouseDown(hit, MouseButton.Left, ModifierKeys.None, 1, point));
            Assert.IsTrue(table.HandleCellMouseDown(hit, MouseButton.Left, ModifierKeys.None, 2, point));
        });
        TestUiDispatcherHost.AwaitTaskOnDispatcher(handler, "candidate list double click handler");
        Task<bool>[] requests = navigation.Since(first);
        Assert.IsTrue(requests.Length > 0, "フォルダノードの選択から画面受付が配送されます。");
        TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(requests), "candidate list folder navigation terminals");
        var bound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool Matches() => ReferenceEquals(main.ItemsSource, viewModel.MainChartList.Rows)
            && viewModel.MainChartList.Rows.Cast<LibraryChartRow>().Select(row => row.Chart.Path).Order(StringComparer.Ordinal)
                .SequenceEqual(expectedPaths.Order(StringComparer.Ordinal));
        var itemsSource = DependencyPropertyDescriptor.FromProperty(CustomTableView.ItemsSourceProperty, typeof(CustomTableView));
        EventHandler changed = (_, _) => { if (Matches()) { bound.TrySetResult(); } };
        itemsSource.AddValueChanged(main, changed);
        try
        {
            if (Matches()) { bound.TrySetResult(); }
            TestUiDispatcherHost.AwaitPresentationOnDispatcher(bound.Task, "candidate list folder rows");
        }
        finally { itemsSource.RemoveValueChanged(main, changed); }
    }

    private static void InvokeRowActivated(MainWindow window, CustomTableView table, DuplicateMergeCandidateRow row)
    {
        CustomTableHitTestResult hit = new(CustomTableHitKind.Cell, row.Index, row, table.Columns.First(column => column.Id == "Path"), 3, Rect.Empty);
        var args = new CustomTableRowRequestedEventArgs(hit, openAtMousePosition: true);
        typeof(MainWindow).GetMethod("customTableDuplicateCandidates_RowActivated", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, [table, args]);
    }

    private sealed class RecordingCandidateShellGateway : IExternalShellGateway
    {
        internal List<string> OpenedDirectoryPaths { get; } = [];

        public void Open(ExternalShellRequest request) { }

        public ExplorerOpenResult OpenFileAndSelect(string filePath) => new();

        public ExplorerOpenResult OpenDirectory(string directoryPath)
        {
            OpenedDirectoryPaths.Add(directoryPath);
            return new ExplorerOpenResult { Kind = ExplorerOpenResultKind.OpenedDirectory, RequestedPath = directoryPath, OpenedPath = directoryPath };
        }

        public bool TryOpenDirectoryWithExplorerProcess(string directoryPath, out string failureReason)
        {
            failureReason = string.Empty;
            return true;
        }
    }

    /// <summary>「優先統合先…」のウィンドウ表示とフォルダ選択を記録する窓口の代替です。ウィンドウは作って検査するだけで、実際には表示しません。</summary>
    private sealed class RecordingPreferredDestinationDialogs : IUiDialogService
    {
        internal List<IReadOnlyList<string>> Opened { get; } = [];

        internal object? NextResult { get; set; }

        internal string[] NextFolders { get; set; } = [];

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
            UiWindowDialogRequest<TWindow, TResult> request, CancellationToken cancellationToken = default)
            where TWindow : Window
        {
            Assert.AreEqual(typeof(DuplicatePreferredDestinationDialog), typeof(TWindow), "所有付き表示の窓口へ渡すウィンドウの型");
            TWindow window = request.CreateWindow();
            var input = (DuplicatePreferredDestinationDialogViewModel)window.DataContext;
            Opened.Add([.. input.Destinations]);
            Assert.IsNotNull(request.Owner, "所有者を指定します。");
            return Task.FromResult((UiWindowDialogResult<TResult>)NextResult!);
        }

        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new UiFolderPickerResult(UiDialogStatus.Accepted, NextFolders));

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("案内は出しません。");

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
