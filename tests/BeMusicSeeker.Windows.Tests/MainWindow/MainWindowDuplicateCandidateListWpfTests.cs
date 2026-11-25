using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.Tests.TreeSelectionDisplayTestSupport;
using ConversionTestSupport = BeMusicSeeker.TestSupport.Components.SelectedChartAudioConversionWorkflowOwnerTestSupport;
using DuplicateSupport = BeMusicSeeker.TestSupport.Components.DuplicateMaintenanceWorkflowOwnerTestSupport;

namespace BeMusicSeeker.Tests;

public sealed partial class MainWindowPackageMaintenanceWpfTests
{
    /// <summary>
    /// 保証：重複ツリーの子の先頭に統合候補一覧のノードが現れ、選ぶと専用の表へ切り替わる。表示中は譜面一覧の表・メインの検索欄・メインの件数表示・
    /// 保留譜面の操作バー・プレイログの集計バーを出さず、専用の検索欄と除外欄を出す。譜面一覧は行0件・選択なし、ツリーの選択表示は一つだけで、
    /// 件数表示は「表示中 n グループ／選択 m グループ」になる。専用の検索欄・除外欄への入力は束縛を通って一覧を絞り込み、件数表示へ反映する。
    /// 表の操作で変えた列幅は一覧の列設定だけに入り、保存される側の列設定（設定の全列設定と、表示中の譜面一覧の列設定）を変えない。
    /// 別の画面へ移ると専用の表・検索欄を隠し、メインの表・検索欄・件数表示を移動先の元の条件で表示し、戻っても状態・並べ替え・検索語・除外語（欄の文字）・列幅を保つ。
    /// 検出する誤り：ノードの位置・欠落、切り替わらない表示、表示中に残るメインの件数表示・バー、検索欄・除外欄と一覧の束縛の欠落、
    /// 隠れた譜面一覧の行の残り、選択表示の二重化、別画面へ移った後の一覧・検索欄・除外欄の残り、列幅の保存側への書込み・共有、再訪での状態・検索欄・除外欄・列幅の消失。
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
            var candidateEditor = (KeywordSearchEditor)window.FindName("duplicateCandidateSearchEditor");
            var candidateExclude = (Border)window.FindName("duplicateCandidateExcludeComposite");
            var excludeEditor = (KeywordSearchEditor)window.FindName("duplicateCandidateExcludeEditor");
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
            Assert.AreEqual(Visibility.Collapsed, candidateExclude.Visibility, "除外欄は一覧の表示中だけ出します。");
            Assert.AreEqual(Visibility.Visible, main.Visibility);
            Assert.AreEqual(Visibility.Visible, mainSummary.Visibility);
            Assert.AreEqual(Resources.Duplicate_merge_candidate_list, node.Header);

            SelectCandidateListAndAwaitView(viewModel, navigation, node);

            Assert.AreEqual(Visibility.Visible, table.Visibility);
            Assert.AreEqual(Visibility.Visible, bar.Visibility);
            Assert.AreEqual(Visibility.Visible, candidateSearch.Visibility, "専用の検索欄を表示します。");
            Assert.AreEqual(Visibility.Visible, candidateExclude.Visibility, "除外欄を表示します。");
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

            // 除外欄への入力は、束縛（遅延あり）を通って、条件に当たる行を持つグループを一覧から除く。選択済みのグループ0は表示外になり、件数の選択数から外れる。
            EnterCandidateText(candidates, excludeEditor, "group0", nameof(DuplicateMergeCandidateListOwner.ExcludeText), () => candidates.ExcludeText);
            Assert.AreEqual(1, candidates.VisibleGroupCount);
            CollectionAssert.AreEqual(new[] { second, second }, table.ItemsSource.Cast<DuplicateMergeCandidateRow>().Select(row => row.State.Group).ToArray(),
                "表は除外後の行集合を表示します。");
            Assert.AreEqual(string.Format(Resources.Duplicate_merge_candidate_summary, 1, 0), summary.Text, "件数表示は除外後の表示中のグループを数えます。");
            EnterCandidateText(candidates, excludeEditor, string.Empty, nameof(DuplicateMergeCandidateListOwner.ExcludeText), () => candidates.ExcludeText);
            Assert.AreEqual(string.Format(Resources.Duplicate_merge_candidate_summary, 2, 1), summary.Text, "除外を解除すると、非表示だったグループの選択も戻ります。");

            // 専用の検索欄への入力は、束縛（遅延あり）を通って一覧を絞り込む。選択済みのグループ0は表示外になり、件数の選択数から外れる。
            EnterCandidateText(candidates, candidateEditor, "group1", nameof(DuplicateMergeCandidateListOwner.SearchText), () => candidates.SearchText);
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

            // 検索で残ったグループ1を除外欄の語でも除く（検索で残り、かつ除外で除かれないグループが表示される）。別の画面から戻った後も保たれる。
            EnterCandidateText(candidates, excludeEditor, "group1", nameof(DuplicateMergeCandidateListOwner.ExcludeText), () => candidates.ExcludeText);
            Assert.AreEqual((0, 0), (candidates.VisibleGroupCount, candidates.SelectedGroupCount));

            // 別の画面（重複グループ）へ移る。専用の表・検索欄を隠し、メインの表・検索欄・件数表示を元の条件で表示する。
            TreeViewItem groupItem = RealizeDuplicateGroupItem(root, second);
            SelectDuplicateTreeAndAwaitRows(viewModel, window, navigation, () => groupItem.IsSelected = true,
                second.ChartFiles.Select(chart => chart.Path).ToArray());
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.IsFalse(candidates.IsViewActive);
            Assert.AreEqual(Visibility.Collapsed, table.Visibility);
            Assert.AreEqual(Visibility.Collapsed, bar.Visibility);
            Assert.AreEqual(Visibility.Collapsed, candidateSearch.Visibility, "専用の検索欄を隠します。");
            Assert.AreEqual(Visibility.Collapsed, candidateExclude.Visibility, "除外欄を隠します。");
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
            Assert.AreEqual("group1", excludeEditor.Text, "再訪で除外欄の文字が戻ります。");
            Assert.AreEqual((0, 0), (candidates.VisibleGroupCount, candidates.SelectedGroupCount), "再訪しても検索の絞込みと除外を保ちます。");
            Assert.AreEqual(("Path", ListSortDirection.Descending, "group1", "group1"),
                (candidates.SortColumnName, candidates.SortDirection, candidates.SearchText, candidates.ExcludeText));
            Assert.AreEqual(statusWidth, candidates.ColumnsSettings.Status.Width, "列幅は起動中だけ保持します。");
            Assert.AreEqual(statusWidth, table.Columns.Single(column => column.Id == "Status").Width, "再訪した表も変更後の列幅を使います。");

            // 除外欄、続いて検索欄を空にすると、保っていた状態（グループ0のSELECT）と降順の並べ替えが全グループに現れる。
            EnterCandidateText(candidates, excludeEditor, string.Empty, nameof(DuplicateMergeCandidateListOwner.ExcludeText), () => candidates.ExcludeText);
            Assert.AreEqual(1, candidates.VisibleGroupCount, "除外を解除すると、検索で残っているグループが現れます。");
            EnterCandidateText(candidates, candidateEditor, string.Empty, nameof(DuplicateMergeCandidateListOwner.SearchText), () => candidates.SearchText);
            Assert.AreEqual(2, candidates.VisibleGroupCount);
            Assert.AreEqual(1, candidates.SelectedGroupCount, "別の画面へ移って戻っても状態を保ちます。");
            Assert.IsTrue(candidates.RowSet.Where(row => row.State.Group == first).All(row => row.State.IsSelected));
            Assert.AreSame(first, candidates.RowSet.Select(row => row.State.Group).Last(), "降順の並べ替えを保ちます。");
            Assert.AreEqual(1, FindDescendants<TreeViewItem>(tree).Count(item => item.IsSelected));
        });
    }

    /// <summary>
    /// 両欄で実エディターの候補を開き、行が0件でも空白付きの値を選択して対応する式へ引用付きで反映できることを確認します。
    /// 言語変更で候補と警告だけを更新し、式・キャレット・行集合を保ちます。警告とクリアが別欄へ及ぶ誤り、通常一覧の保存式の表示・変更も検出します。
    /// 対象を識別する従来のUI Automation識別子と現在の言語のアクセシブル名は、実際の入力欄で確認します。
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CandidateList_SearchEditorsKeepCompletionAndDiagnosticsIndependent(bool exclude)
    {
        RunDuplicateSelectionFixture((scope, viewModel, window, library, charts, navigation) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            var root = (TreeViewItem)window.FindName("treeViewItemSearchDuplicated");
            var table = (CustomTableView)window.FindName("customTableDuplicateCandidates");
            var normal = (KeywordSearchEditor)window.FindName("KeywordSearchEditor");
            var search = (KeywordSearchEditor)window.FindName("duplicateCandidateSearchEditor");
            var exclusion = (KeywordSearchEditor)window.FindName("duplicateCandidateExcludeEditor");
            KeywordSearchSavedQueryOwner normalSaved = viewModel.ChartFilters.KeywordSearchAssistanceOwner.SavedQueryOwner;
            Assert.IsTrue(normalSaved.TryAddFavorite("title:pinned").Succeeded);
            Assert.IsTrue(normalSaved.TryCommitHistory("title:recent").Succeeded);
            FocusCandidateEditor(normal);
            Assert.IsTrue(normal.Presentation.VisibleItems.Any(item => item.Query == "title:pinned"));
            Assert.IsTrue(normal.Presentation.VisibleItems.Any(item => item.Query == "title:recent"));
            normal.ClearKeyboardFocusIfOutside(table);
            KeywordSearchSavedQueryState saved = normalSaved.State;

            SelectCandidateListAndAwaitView(viewModel, navigation, RealizeCandidateListNode(root));
            DuplicateMergeCandidateListOwner candidates = viewModel.DuplicateMergeCandidateList;
            KeywordSearchEditor target = exclude ? exclusion : search;
            KeywordSearchEditor other = exclude ? search : exclusion;
            string targetProperty = exclude ? nameof(candidates.ExcludeText) : nameof(candidates.SearchText);
            string otherProperty = exclude ? nameof(candidates.SearchText) : nameof(candidates.ExcludeText);
            string TargetValue() => exclude ? candidates.ExcludeText : candidates.SearchText;
            string OtherValue() => exclude ? candidates.SearchText : candidates.ExcludeText;
            string TargetFieldName() => exclude ? Resources.Duplicate_merge_candidate_exclude_search : Resources.Duplicate_merge_candidate_search;
            string targetAutomationId = exclude ? "DuplicateCandidateExclude" : "DuplicateCandidateSearch";
            var warning = (AppFontIcon)window.FindName(exclude ? "duplicateCandidateExcludeWarningIcon" : "duplicateCandidateSearchWarningIcon");
            var otherWarning = (AppFontIcon)window.FindName(exclude ? "duplicateCandidateSearchWarningIcon" : "duplicateCandidateExcludeWarningIcon");
            var clear = (AppFontIcon)window.FindName(exclude ? "duplicateCandidateExcludeClearIcon" : "duplicateCandidateSearchClearIcon");

            string previous = Resources.Culture?.Name ?? "ja-JP";
            try
            {
                ResourceService.Current.ChangeCulture("en-US");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(targetAutomationId, AutomationProperties.GetAutomationId(target.InputTextBoxControl));
                Assert.AreNotEqual(targetAutomationId, AutomationProperties.GetAutomationId(target));
                Assert.AreEqual(TargetFieldName(), AutomationProperties.GetName(target.InputTextBoxControl));
                FocusCandidateEditor(target);
                CollectionAssert.AreEquivalent(new[] { "path:", "folder:", "title:", "artist:", "status:" },
                    target.Presentation.VisibleItems.Select(item => item.DisplayText).ToArray());
                Assert.IsFalse(other.AssistancePopupControl.IsOpen, "候補は操作中の欄にだけ開きます。");

                const string otherText = "folder:NoSuchFolder";
                EnterCandidateText(candidates, other, otherText, otherProperty, OtherValue);
                const string prefix = "status:";
                EnterCandidateText(candidates, target, prefix, targetProperty, TargetValue);
                target.InputTextBoxControl.CaretIndex = prefix.Length;
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(0, table.ItemsSource.Count);
                Assert.IsTrue(target.AssistancePopupControl.IsOpen);
                Assert.AreEqual("Cannot merge", target.Presentation.VisibleItems[3].DisplayText);
                Assert.AreEqual(Visibility.Visible, warning.Visibility);
                Assert.AreEqual(Visibility.Collapsed, otherWarning.Visibility);
                object rows = table.ItemsSource;
                object englishWarning = warning.ToolTip;

                ResourceService.Current.ChangeCulture("ja-JP");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual("統合不可", target.Presentation.VisibleItems[3].DisplayText);
                Assert.AreEqual(targetAutomationId, AutomationProperties.GetAutomationId(target.InputTextBoxControl));
                Assert.AreEqual(TargetFieldName(), AutomationProperties.GetName(target.InputTextBoxControl));
                Assert.AreNotEqual(englishWarning, warning.ToolTip);
                Assert.AreEqual(prefix, target.Text);
                Assert.AreEqual(prefix.Length, target.InputTextBoxControl.CaretIndex);
                Assert.AreSame(rows, table.ItemsSource, "言語を変更しても絞り込みを再実行しません。");
                Assert.AreEqual(otherText, other.Text);

                ResourceService.Current.ChangeCulture("en-US");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual("Cannot merge", target.Presentation.VisibleItems[3].DisplayText);
                Assert.IsTrue(target.HandleInputKey(Key.Space, ModifierKeys.Control));
                for (int i = 0; i < 3; i++) { Assert.IsTrue(target.HandleInputKey(Key.Down, ModifierKeys.None)); }
                const string completed = "status:\"Cannot merge\" ";
                AwaitCandidateText(candidates, completed, targetProperty, TargetValue, () =>
                {
                    Assert.IsTrue(target.HandleInputKey(Key.Enter, ModifierKeys.None));
                    Assert.AreEqual(completed, target.InputTextBoxControl.Text);
                });
                Assert.AreEqual(otherText, other.Text);
                Assert.AreEqual(Visibility.Collapsed, warning.Visibility);
                target.ClearKeyboardFocusIfOutside(table);
                Assert.AreSame(saved, normalSaved.State, "入力が残った状態で離れても通常一覧の保存式を変更しません。");

                EnterCandidateText(candidates, target, "genre:invalid", targetProperty, TargetValue);
                Assert.AreEqual(Visibility.Visible, warning.Visibility);
                Assert.AreEqual(Visibility.Collapsed, otherWarning.Visibility);
                Assert.AreEqual(Visibility.Visible, clear.Visibility);
                AwaitCandidateText(candidates, string.Empty, targetProperty, TargetValue, () =>
                {
                    clear.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    {
                        RoutedEvent = UIElement.MouseLeftButtonDownEvent,
                    });
                    Assert.AreEqual(string.Empty, target.InputTextBoxControl.Text, "クリアは同期で入力欄へ反映します。");
                });
                Assert.AreEqual(Visibility.Collapsed, warning.Visibility);
                Assert.AreEqual(Visibility.Hidden, clear.Visibility);
                Assert.AreEqual(otherText, OtherValue());
                target.ClearKeyboardFocusIfOutside(table);
                Assert.AreSame(saved, normalSaved.State, "候補の適用・クリア・離脱で通常一覧の保存式を変更しません。");
            }
            finally
            {
                target.ClearKeyboardFocusIfOutside(table);
                ResourceService.Current.ChangeCulture(previous);
                TestUiDispatcherHost.ProcessQueuedPresentation();
            }
        });
    }

    private static void FocusCandidateEditor(KeywordSearchEditor editor)
    {
        TextBox input = editor.InputTextBoxControl;
        input.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, null, input)
        {
            RoutedEvent = Keyboard.GotKeyboardFocusEvent,
        });
        TestUiDispatcherHost.ProcessQueuedPresentation();
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
            var candidateExclude = (Border)window.FindName("duplicateCandidateExcludeComposite");
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
            AssertCandidateListHidden(table, candidateSearch, candidateExclude, bar);
            Assert.AreEqual(Visibility.Visible, pendingBar.Visibility, "保留の画面は保留譜面の操作バーを出します。");
            Assert.AreEqual(Visibility.Collapsed, historyBar.Visibility);
            Assert.AreEqual(Visibility.Visible, mainSummary.Visibility, "保留の画面はメインの件数表示を出します。");

            SelectCandidateListAndAwaitView(viewModel, navigation, RealizeCandidateListNode(root));
            TestUiDispatcherHost.ProcessQueuedPresentation();
            AssertCandidateListHidesMainBars(mainSummary, pendingBar, historyBar);

            // プレイログの画面へ移る。
            SelectPlayHistoryAndAwaitView(viewModel, historyRoot);
            Assert.IsFalse(candidates.IsViewActive);
            AssertCandidateListHidden(table, candidateSearch, candidateExclude, bar);
            Assert.AreEqual(Visibility.Visible, historyBar.Visibility, "プレイログの画面は集計バーを出します。");
            Assert.AreEqual(Visibility.Collapsed, pendingBar.Visibility);
            Assert.AreEqual(Visibility.Collapsed, mainSummary.Visibility, "プレイログの画面はメインの件数表示を出しません。");

            SelectCandidateListAndAwaitView(viewModel, navigation, RealizeCandidateListNode(root));
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.IsFalse(viewModel.PlayHistory.IsViewActive);
            AssertCandidateListHidesMainBars(mainSummary, pendingBar, historyBar);
            Assert.AreEqual(Visibility.Visible, table.Visibility);
            Assert.AreEqual(Visibility.Visible, candidateSearch.Visibility);
            Assert.AreEqual(Visibility.Visible, candidateExclude.Visibility);
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
            Assert.AreEqual(1, table.SelectedIndex, "主行はクリックした行です。");
            CollectionAssert.AreEquivalent(new[] { rows[0], rows[1] }, table.GetSelectedRowsSnapshot().ToArray(), "SELECTのクリックは、そのグループの全行を選びます。");
            Click(table, 1, "Select", clickCount: 2);
            Assert.IsTrue(rows[0].State.IsSelected, "ダブルクリックは操作として二回数え、元の状態へ戻ります。");
            Assert.AreEqual(requestsBefore, navigation.Count, "有効なSELECTのダブルクリックは遷移を起こしません。");
            Click(table, 1, "Select");
            Assert.IsFalse(rows[0].State.IsSelected);
            Click(table, 1, "Keep");
            Assert.AreEqual(rows[1].Path, rows[0].State.KeepFolder);
            Assert.IsFalse(rows[0].IsKeep);
            CollectionAssert.AreEqual(new[] { rows[1] }, table.GetSelectedRowsSnapshot().ToArray(), "KEEPのクリックは、結合列で選んだグループの全行ではなく、押した行だけを選びます。");
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
            DuplicateMergeCandidateRow vanishedGroup = new(orphanState, @"C:\Nowhere\a", "a", string.Empty, string.Empty, 0, 0, () => default);
            DuplicateMergeCandidateRow vanishedFolder = new(candidates.RowSet[0].State, @"C:\Nowhere\other", "other", string.Empty, string.Empty, 0, 0, () => default);
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
    /// 保証：ツールバーの五つのボタンとチェックボックスは共通スタイルの見た目を保つ。表示言語を変えると、ノードの見出し・STATUS・件数・ボタン・メニュー・除外欄の名前と
    /// プレースホルダーが現在の言語に追従する。終わったら元の言語へ戻す。
    /// 検出する誤り：画面内の旧スタイル（個別の配色・テンプレート・無効時の不透明度）の残り、標準部品のままのチェックボックス、
    /// 起動時の言語のまま残る文言、言語の変更で一覧の状態（SELECT）が失われる誤り。
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
            var excludeEditor = (KeywordSearchEditor)window.FindName("duplicateCandidateExcludeEditor");
            var excludePlaceholder = (TextBlock)window.FindName("duplicateCandidateExcludePlaceholder");
            var menu = (ContextMenu)window.FindResource("duplicateCandidateContextMenu");
            DuplicateMergeCandidateListOwner candidates = viewModel.DuplicateMergeCandidateList;
            TreeViewItem node = RealizeCandidateListNode(root);
            SelectCandidateListAndAwaitView(viewModel, navigation, node);
            // ツールバーの五つのボタンとチェックボックスは、共通スタイルの見た目（配色・テンプレート）を保つ。
            AssertToolbarKeepsCanonicalPresentation(
                [
                    select,
                    (Button)window.FindName("duplicateCandidateClearVisibleButton"),
                    (Button)window.FindName("duplicateCandidatePreferredDestinationsButton"),
                    verify,
                    (Button)window.FindName("duplicateCandidateOrganizeButton")
                ],
                quickCompare);
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
                Assert.AreEqual(Resources.Duplicate_merge_candidate_exclude_search, AutomationProperties.GetName(excludeEditor.InputTextBoxControl), "除外欄のアクセシブル名は現在の言語です。");
                Assert.AreEqual(Resources.Duplicate_merge_candidate_exclude_placeholder, excludePlaceholder.Text, "除外欄のプレースホルダーは現在の言語です。");
                Assert.IsTrue(menu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, Resources.Duplicate_merge_candidate_verify_group)));
                Assert.AreEqual(
                    string.Format(Resources.Duplicate_merge_candidate_status_reasons_format, Resources.Duplicate_merge_candidate_status_caution, Resources.Duplicate_merge_candidate_reason_parent_reference),
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
    /// 確認を終えたグループのSELECTは段階によらずオフになり（件数表示の選択は0）、進捗の要求はメインウィンドウを所有者として出して、結果の案内を一件だけ出す（「チェックを入れました」の文を含まない）。簡易チェックは、開始時にオフで案内がなく、入れる操作で警告を一件出して管理主体へ値を渡し、外す操作では案内しない。
    /// チェックに触れずに確認すると、サイズと更新日時が同じで内容が異なる資源のグループは要注意になり（既定のオフが検査器へ届く）、そのツールチップは標準構成の上書きの設定に合った扱いを添える。
    /// 衝突を解消して右クリックで再確認すれば安全になるが、SELECTはオフのまま操作できる。
    /// 標準構成の接続は、共通の受付が使用中なら確認がBusyの案内で未開始になることで確かめる。条件の網羅は検査器・管理主体のケースが担当する。
    /// 検出する誤り：ボタン・メニュー・チェックの未接続、進捗の所有者の欠落、案内の重複・欠落、SELECT・件数表示・STATUSの更新漏れ、標準構成の受付への未接続、
    /// チェックの既定がオン・起動時の案内、既定の値が検査器へ届かない、ツールチップが構成の設定を読まない。
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
            // サイズと更新日時をそろえる。簡易チェックが有効なら読まずに同一内容になるため、要注意になれば既定のオフが検査器へ届いている。
            DateTime sameTime = new(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(Path.Combine(rows[2].Path, "x.bin"), sameTime);
            File.SetLastWriteTimeUtc(Path.Combine(rows[3].Path, "x.bin"), sameTime);
            // 同一内容の資源を共有させ、連結の不足（同一内容ファイルなし）を別の理由として混ぜない。
            File.WriteAllBytes(Path.Combine(rows[2].Path, "shared.bin"), [9, 9, 9]);
            File.WriteAllBytes(Path.Combine(rows[3].Path, "shared.bin"), [9, 9, 9]);
            Click(table, 0, "Select");
            Click(table, 2, "Select");
            Assert.AreEqual(2, candidates.SelectedGroupCount);
            int messagesBefore = dialogs.Messages.Count;
            Assert.IsFalse(quickCompare.IsChecked, "開始時はチェックが外れています。");
            Assert.IsFalse(candidates.QuickCompareEnabled);
            Assert.IsFalse(dialogs.Messages.Any(message => message.MessageBoxText == Resources.Duplicate_merge_candidate_quick_compare_notice), "起動から開始までに警告の案内は出ません。");

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
                string.Format(Resources.Duplicate_merge_candidate_status_reasons_format, Resources.Duplicate_merge_candidate_status_caution, Resources.Duplicate_merge_candidate_reason_content_conflict), rows[2].StatusText);
            Assert.IsFalse(rows[0].State.IsSelected, "安全のSELECTも確認で消費してオフにします。");
            Assert.IsFalse(rows[2].State.IsSelected, "要注意のSELECTはオフです。");
            Assert.AreEqual(0, candidates.SelectedGroupCount);
            Assert.AreEqual(string.Format(Resources.Duplicate_merge_candidate_summary, 2, 0), summary.Text, "件数表示の選択は0です。");
            Assert.AreEqual(Resources.Duplicate_merge_candidate_verify_progress_title, dialogs.ProgressRequest!.Title);
            Assert.AreSame(window, dialogs.ProgressRequest.Owner, "進捗の所有者はメインウィンドウです。");
            Assert.AreEqual(messagesBefore + 1, dialogs.Messages.Count, "結果の案内は一件です。");
            Assert.AreEqual(string.Format(Resources.Duplicate_merge_candidate_verify_result_format, 2, 1, 1, 0), dialogs.Messages[^1].MessageBoxText);
            Assert.AreEqual("2グループを確認しました。安全 1／要注意 1／統合不可 0。", dialogs.Messages[^1].MessageBoxText, "ja-JP の案内は「チェックを入れました」の文を含みません。");
            string tooltip = ResolveStatusTooltip(table, 2);
            StringAssert.Contains(tooltip, Resources.Duplicate_merge_candidate_reason_content_conflict_description);
            StringAssert.Contains(tooltip, Path.Combine(rows[2].Path, "x.bin"));
            // 標準構成は、統合が使うのと同じ設定をツールチップへ渡す。この構成の設定はスマート上書きがオン（既定）で、設定ごとの分岐は管理主体のケースが担当する。
            Assert.IsTrue(MainWindowViewModelTestFactory.GetComposition(viewModel).BmsLibraryOptionsProvider().EnableSmartComponentOverwrite, "前提：構成の設定はスマート上書きがオンです。");
            StringAssert.Contains(tooltip, Resources.Duplicate_merge_candidate_overwrite_note_newer);
            Assert.IsFalse(tooltip.Contains(Resources.Duplicate_merge_candidate_overwrite_note_source, StringComparison.Ordinal));
            StringAssert.Contains(ResolveStatusTooltip(table, 0), Resources.Duplicate_merge_candidate_safe_not_compared);

            // 右クリックメニュー：衝突を解消してグループ1だけを再確認する。SELECTの状態によらず対象になり、安全になってもSELECTはオフのまま操作できる。
            File.WriteAllBytes(Path.Combine(rows[3].Path, "x.bin"), [1, 2, 3, 4]);
            table.SelectRowsByPredicate(row => row is DuplicateMergeCandidateRow { Index: 2 });
            MenuItem verifyGroup = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, Resources.Duplicate_merge_candidate_verify_group));
            messagesBefore = dialogs.Messages.Count;
            AwaitHandler(() => RaiseMenuClick(verifyGroup), "verify-menu");
            Assert.AreEqual(Resources.Duplicate_merge_candidate_status_safe, rows[2].StatusText);
            Assert.IsFalse(rows[2].State.IsSelected, "再確認で安全になっても、SELECTはオフのままです。");
            Assert.IsTrue(rows[2].State.CanSelect, "再確認の後も操作できる状態です。");
            Click(table, 2, "Select");
            Assert.IsTrue(rows[2].State.IsSelected, "オフのSELECTを利用者の操作でオンにできます。");
            Click(table, 2, "Select");
            Assert.IsFalse(rows[2].State.IsSelected);
            Assert.AreEqual(messagesBefore + 1, dialogs.Messages.Count);
            StringAssert.Contains(ResolveStatusTooltip(table, 2), string.Format(Resources.Duplicate_merge_candidate_safe_compared_single_format, 2, Resources.Duplicate_merge_candidate_method_full));

            // 簡易チェックを入れる：入れる操作で警告を一件出し、管理主体の値を有効にする。外す操作では案内しない。
            messagesBefore = dialogs.Messages.Count;
            quickCompare.IsChecked = true;
            AwaitHandler(() => quickCompare.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)), "quick-compare-on");
            Assert.IsTrue(candidates.QuickCompareEnabled);
            Assert.AreEqual(messagesBefore + 1, dialogs.Messages.Count);
            Assert.AreEqual(Resources.Duplicate_merge_candidate_quick_compare_notice, dialogs.Messages[^1].MessageBoxText);
            quickCompare.IsChecked = false;
            AwaitHandler(() => quickCompare.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)), "quick-compare-off");
            Assert.IsFalse(candidates.QuickCompareEnabled);
            Assert.AreEqual(messagesBefore + 1, dialogs.Messages.Count, "外す操作では案内しません。");
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
        if (node == null)
        {
            // 子の項目の選択でツリーがスクロールした後は、先頭のノードが仮想化で未生成になるため、公開APIで生成する。
            FindItemsHostPanel(root).BringIndexIntoViewPublic(0);
            root.UpdateLayout();
            node = root.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem;
        }
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

    private static void AssertCandidateListHidden(CustomTableView table, Border candidateSearch, Border candidateExclude, Border bar)
    {
        Assert.AreEqual(Visibility.Collapsed, table.Visibility, "一覧の表を隠します。");
        Assert.AreEqual(Visibility.Collapsed, candidateSearch.Visibility, "専用の検索欄を隠します。");
        Assert.AreEqual(Visibility.Collapsed, candidateExclude.Visibility, "除外欄を隠します。");
        Assert.AreEqual(Visibility.Collapsed, bar.Visibility, "一覧のツールバーを隠します。");
    }

    /// <summary>
    /// 専用の検索欄または除外欄へ文字を入れ、束縛（遅延あり）が一覧の対応する語へ届くまで待ちます。固定時間は待たず、そのプロパティの変更通知を待ちます。
    /// 語の設定は行集合の作り直しまで同期で行うため、通知の後は絞り込んだ行集合を読めます。
    /// </summary>
    /// <param name="propertyName">対象の語のプロパティ名（検索語または除外語）。</param>
    /// <param name="currentValue">対象の語の現在値の取得。</param>
    private static void EnterCandidateText(
        DuplicateMergeCandidateListOwner owner, KeywordSearchEditor editor, string text, string propertyName, Func<string> currentValue)
        => AwaitCandidateText(owner, text, propertyName, currentValue,
            () => editor.InputTextBoxControl.SetCurrentValue(TextBox.TextProperty, text));

    private static void AwaitCandidateText(
        DuplicateMergeCandidateListOwner owner, string text, string propertyName, Func<string> currentValue, Action input)
    {
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler changed = (_, args) =>
        {
            if (args.PropertyName == propertyName && currentValue() == text) { applied.TrySetResult(); }
        };
        owner.PropertyChanged += changed;
        try
        {
            Assert.AreNotEqual(text, currentValue(), "前提：入力前の語は異なります。");
            input();
            TestUiDispatcherHost.AwaitPresentationOnDispatcher(applied.Task, "candidate text binding " + propertyName);
        }
        finally { owner.PropertyChanged -= changed; }
        TestUiDispatcherHost.ProcessQueuedPresentation();
    }

    /// <summary>
    /// ツールバーのボタンとチェックボックスが、共通スタイルの見た目を保つことを確かめます。スタイルの継承元に共通スタイルを含み、実際に使うテンプレートが共通のものと同じで、
    /// 共通スタイルより上の派生スタイル（Setter・Trigger）とコントロールへの直接指定が、テンプレート・配色・無効時の不透明度などを上書きしていないことを見ます。
    /// 高さ・余白・Margin・有効条件の差は許容します。実際の配色とテーマへの追従は、共通スタイルの既存テストが担当します。
    /// </summary>
    private static void AssertToolbarKeepsCanonicalPresentation(IEnumerable<Button> buttons, CheckBox? checkBox = null)
    {
        foreach (Button button in buttons)
        {
            AssertKeepsCanonicalPresentation(
                button, "App.Canonical.CompactButtonStyle", "App.Canonical.ButtonTemplate",
                [Control.TemplateProperty, Control.BackgroundProperty, Control.ForegroundProperty, Control.BorderBrushProperty, UIElement.OpacityProperty]);
        }
        if (checkBox != null)
        {
            AssertKeepsCanonicalPresentation(
                checkBox, "App.Canonical.CheckBoxStyle", "App.Canonical.CheckBoxTemplate",
                [Control.TemplateProperty, Control.BackgroundProperty, Control.BorderBrushProperty]);
        }
    }

    private static void AssertKeepsCanonicalPresentation(
        Control control, string canonicalStyleKey, string canonicalTemplateKey, DependencyProperty[] protectedProperties)
    {
        string label = $"{control.Name}（{canonicalStyleKey}）";
        var canonical = (Style)control.FindResource(canonicalStyleKey);
        var canonicalTemplate = (ControlTemplate)control.FindResource(canonicalTemplateKey);
        var derived = new List<Style>();
        Style? current = control.Style;
        while (current != null && !ReferenceEquals(current, canonical))
        {
            derived.Add(current);
            current = current.BasedOn;
        }
        Assert.IsNotNull(current, $"{label}: スタイルの継承元に共通スタイルを含みます。");
        Assert.AreSame(canonicalTemplate, control.Template, $"{label}: 実際に使うテンプレートは共通スタイルのものです。");
        foreach (Style style in derived)
        {
            foreach (Setter setter in EnumerateSetters(style))
            {
                Assert.IsFalse(protectedProperties.Contains(setter.Property), $"{label}: 派生スタイルが {setter.Property?.Name} を上書きしています。");
            }
        }
        foreach (DependencyProperty property in protectedProperties)
        {
            Assert.AreEqual(DependencyProperty.UnsetValue, control.ReadLocalValue(property), $"{label}: {property.Name} の直接指定があります。");
        }
    }

    /// <summary>スタイルが直接持つSetterと、Trigger・DataTrigger・MultiTrigger・MultiDataTriggerのSetterを列挙します。</summary>
    private static IEnumerable<Setter> EnumerateSetters(Style style)
    {
        foreach (Setter setter in style.Setters.OfType<Setter>()) { yield return setter; }
        foreach (TriggerBase trigger in style.Triggers)
        {
            SetterBaseCollection? setters = trigger switch
            {
                Trigger t => t.Setters,
                DataTrigger t => t.Setters,
                MultiTrigger t => t.Setters,
                MultiDataTrigger t => t.Setters,
                _ => null
            };
            foreach (Setter setter in (setters ?? []).OfType<Setter>()) { yield return setter; }
        }
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

    /// <summary>実Dispatcherで、統合候補一覧の行集合が指定の行数で採用されるまで待ちます。固定時間は待たず、行集合の入替え通知（Rows）を待ちます。</summary>
    private static void AwaitCandidateRows(DuplicateMergeCandidateListOwner owner, int expectedRowCount)
        => CandidateListAwaitSupport.AwaitRows(owner, expectedRowCount);

    /// <summary>
    /// 保証：実画面の「選択グループを整理」ボタンから、実ワークフロー・実際の保存窓口・実ファイル・SQLiteで、選択した二つのグループを一括整理する。
    /// 実行の開始（再生停止とモデルの呼出しの前）では共通の受付を保持し、画面下部の進捗行は出さず、進捗画面は所有者つきで出す。
    /// 終了後は、解消したグループが一覧の行から消え、影響しなかったグループは同じインスタンスのままSELECT・KEEP・確認結果を保ち、
    /// 統合候補一覧のノードの選択と表示中の状態を保って譜面一覧を出さない。変更はファイル・DBへ反映され、Busy案内は出ず、結果は一回だけ案内し、受付と活動を解放する。
    /// 一覧の作り直しは結果の案内との前後を固定せず、採用まで待って確かめる。
    /// 受付の保持中の重複選択がBusyにならず準備へ合流する保証は、このケースでは直接確かめず（Busy案内が出ないことを確かめるだけ）、
    /// <c>AcceptedChangeDuplicatePreparationTests</c> の一括整理の行が担う。
    /// 検出する誤り：ボタンの未接続、受付の保持漏れ、下部の進捗行の表示、再構築での状態（SELECT・KEEP・確認結果）の消失・インスタンスの置換、
    /// 解消したグループの行の残り、別の画面への自動移動、結果の重複、解放漏れ。
    /// </summary>
    [TestMethod]
    public void CandidateList_OrganizeButtonMergesTheSelectedGroupsAndRebuildsTheListKeepingTheRest()
    {
        var dialogs = new DuplicateSupport.FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
        };
        RunDuplicateSelectionFixture((scope, viewModel, window, library, charts, navigation) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            var root = (TreeViewItem)window.FindName("treeViewItemSearchDuplicated");
            var table = (CustomTableView)window.FindName("customTableDuplicateCandidates");
            var main = (CustomTableView)window.FindName("customTableView");
            DuplicateMergeCandidateListOwner candidates = viewModel.DuplicateMergeCandidateList;
            ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(viewModel);
            SelectCandidateListAndAwaitView(viewModel, navigation, RealizeCandidateListNode(root));
            DuplicateMergeCandidateRow[] rows = [.. candidates.RowSet];
            Assert.AreEqual(6, rows.Length, "二つの整理対象と、影響しない一つのグループ");
            string firstKeep = rows[0].Path, firstSource = rows[1].Path, secondSource = rows[3].Path;
            DuplicateMergeCandidateGroupState untouched = rows[4].State;

            // 影響しない側（三つ目のグループ）は、KEEPを変え、要注意の確認結果を書いておく。整理する側（一・二つ目）だけSELECTをオンにする。
            Click(table, 5, "Keep");
            var cautionResult = new DuplicateMergeVerificationResult(
                DuplicateMergeVerificationStage.Caution,
                [new DuplicateMergeVerificationReason(DuplicateMergeVerificationReasonKind.ParentReference, [new DuplicateMergeFinding(rows[4].Path, "c.bms", "../x.wav")])]);
            candidates.WriteVerifications([new DuplicateMergeVerificationWrite(untouched.Group, cautionResult)]);
            // 整理する側の一つ目へ、安全で.bmx/.txtの衝突の付記を持つ確認結果を書く。確認文の衝突の行が、標準構成の上書きの設定に接続していることを確かめる。
            candidates.WriteVerifications(
                [new DuplicateMergeVerificationWrite(rows[0].State.Group, new DuplicateMergeVerificationResult(DuplicateMergeVerificationStage.Safe, [], ["readme.txt"]))]);
            Click(table, 0, "Select");
            Click(table, 2, "Select");
            Assert.AreEqual(2, candidates.SelectedGroupCount);
            string untouchedKeep = untouched.KeepFolder!;
            Assert.AreEqual(rows[5].Path, untouchedKeep, "前提：影響しない側のKEEPを変えています。");
            int progressRowsBefore = viewModel.ProgressHub.Rows.Count;

            // 変更の活動の開始（実行側の最初の処理。再生停止とモデルの呼出しの前）で止める。このとき進捗画面は開いており、受付は保持され、確定の前である。
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            viewModel.ChartMutationActivity.ActivityChanged += (_, _) =>
            {
                if (viewModel.ChartMutationActivity.IsActive) { reached.TrySetResult(); release.Wait(TimeSpan.FromSeconds(60)); }
            };
            Task handler = MainWindowEventCompletion.CaptureAsyncEventCompletion(() => ClickButton(window, "duplicateCandidateOrganizeButton"));
            try
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(reached.Task, handler, "organize in progress"), "organize in progress");
                Assert.IsFalse(handler.IsCompleted, "実行の途中では、操作は終わっていません。");
                Assert.IsTrue(Directory.Exists(firstSource) && Directory.Exists(secondSource), "前提：確定の前で、統合元の後片付けはこれからです。");
                Assert.IsFalse(composition.OperationAdmission.CanEnter, "実行の途中は、共通の受付を保持します。");
                Assert.AreEqual(progressRowsBefore, viewModel.ProgressHub.Rows.Count, "画面下部の進捗行は出しません。");
                Assert.AreEqual(1, dialogs.Progress.Runs);
                Assert.AreSame(window, dialogs.Progress.Request!.Owner, "進捗画面の所有者はメインウィンドウです。");
                Assert.AreEqual(Resources.Duplicate_batch_organize_title, dialogs.Progress.Request.Title);
                Assert.IsNotNull(dialogs.ConfirmationRequest, "承認の後に実行しています。");
                BmsLibraryOptionsSnapshot options = composition.BmsLibraryOptionsProvider();
                Assert.IsTrue(options.EnableSmartComponentOverwrite && !options.KeepSmartOverwriteProtectedFilesByRenaming, "前提：構成の設定はスマート上書きだけがオンです。");
                // もう一方の整理対象は未確認なので、警告用の本文は［未確認の行、衝突の行］です。
                CollectionAssert.AreEqual(
                    new[]
                    {
                        string.Format(Resources.Duplicate_batch_organize_confirm_unverified_format, 1),
                        Resources.Duplicate_batch_organize_confirm_collision_newer
                    },
                    dialogs.ConfirmationRequest.WarningMessageBoxText!.Split(Environment.NewLine),
                    "確認文の衝突の行は、統合が使うのと同じ構成の設定で決まります。");
                Assert.AreEqual(0, dialogs.Messages.Count, "結果は終了後に案内します。");
            }
            finally
            {
                release.Set();
            }
            TestUiDispatcherHost.AwaitTaskOnDispatcher(handler, "organize handler terminal");
            AwaitCandidateRows(candidates, 2);
            TestUiDispatcherHost.ProcessQueuedPresentation();

            // 一覧：解消したグループは行から消え、影響しなかったグループは同じインスタンスで状態を保つ。
            Assert.IsTrue(candidates.IsViewActive, "別の画面へ自動で移らず、一覧を表示したままです。");
            DuplicateMergeCandidateRow[] after = [.. candidates.RowSet];
            CollectionAssert.AreEqual(new[] { rows[4].Path, rows[5].Path }, after.Select(row => row.Path).ToArray());
            Assert.AreSame(untouched, after[0].State, "影響しなかったグループは同じ状態のインスタンスです。");
            Assert.AreEqual(untouchedKeep, untouched.KeepFolder, "KEEPを保ちます。");
            Assert.AreSame(cautionResult, untouched.Verification, "確認結果を保ちます。");
            Assert.IsFalse(untouched.IsSelected);
            Assert.AreEqual(0, candidates.SelectedGroupCount);
            Assert.AreEqual(Visibility.Visible, table.Visibility);
            Assert.AreEqual(Visibility.Collapsed, main.Visibility, "譜面一覧は出しません。");
            Assert.AreEqual(0, viewModel.MainChartList.Rows.Count);
            Assert.IsTrue(RealizeCandidateListNode(root).IsSelected, "統合候補一覧のノードの選択を保ちます。");

            // 実ファイル・DB・受付・案内。
            Assert.IsFalse(Directory.Exists(firstSource) || Directory.Exists(secondSource), "統合元のフォルダは削除されます。");
            Assert.IsTrue(File.Exists(Path.Combine(firstKeep, "chart.bms")), "統合先の譜面は残ります。");
            Assert.IsTrue(rows.Skip(4).All(row => Directory.Exists(row.Path) && File.Exists(Path.Combine(row.Path, "chart.bms"))), "影響しないグループのファイルは変わりません。");
            string songDb = Path.Combine(Path.GetDirectoryName(firstKeep)!, "song.db");
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(library.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), songDb).OpenSongDbReadOnly())
            {
                string[] paths = [.. db.Table<LR2SongDB.song>().ToList().Select(song => song.path)];
                Assert.AreEqual(4, paths.Length, "統合元の同一内容の譜面の行は、DBから除かれます。");
                Assert.IsFalse(paths.Any(path => path.StartsWith(firstSource, StringComparison.OrdinalIgnoreCase) || path.StartsWith(secondSource, StringComparison.OrdinalIgnoreCase)));
            }
            UiMessageRequest result = dialogs.Messages.Single();
            Assert.AreEqual(Resources.Duplicate_batch_organize_title, result.Caption, "結果は一回だけで、Busy案内を含みません。");
            Assert.AreEqual(string.Format(Resources.Duplicate_batch_organize_result_format, 2, 2, 0, 0, 0, 0), result.MessageBoxText.Split(Environment.NewLine)[0]);
            Assert.IsFalse(viewModel.ChartMutationActivity.IsActive);
            Assert.IsTrue(composition.OperationAdmission.TryEnter(out IDisposable? next), "受付を解放します。");
            next.Dispose();
        }, dialogService: dialogs, fillerGroups: 1);
    }

    /// <summary>
    /// 保証：全グループを整理して結果が0件になったとき、一覧の古い行を残さず、結果の案内は一回で、ほかの通知は出さず、受付を解放する。
    /// 検出する誤り：解消済みのグループの行の残り、0件での失敗・Busyの通知、受付の解放漏れ。
    /// </summary>
    [TestMethod]
    public void CandidateList_OrganizingEveryGroupLeavesNoStaleRows()
    {
        var dialogs = new DuplicateSupport.FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
        };
        RunDuplicateSelectionFixture((scope, viewModel, window, library, charts, navigation) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            var root = (TreeViewItem)window.FindName("treeViewItemSearchDuplicated");
            DuplicateMergeCandidateListOwner candidates = viewModel.DuplicateMergeCandidateList;
            ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(viewModel);
            SelectCandidateListAndAwaitView(viewModel, navigation, RealizeCandidateListNode(root));
            Assert.AreEqual(4, candidates.RowSet.Count);
            ClickButton(window, "duplicateCandidateSelectVisibleButton");
            Assert.AreEqual(2, candidates.SelectedGroupCount);

            AwaitHandler(() => ClickButton(window, "duplicateCandidateOrganizeButton"), "organize every group");
            AwaitCandidateRows(candidates, 0);
            TestUiDispatcherHost.ProcessQueuedPresentation();

            Assert.AreEqual(0, candidates.RowSet.Count, "古い行を残しません。");
            Assert.AreEqual(0, candidates.VisibleGroupCount);
            Assert.AreEqual(0, candidates.SelectedGroupCount);
            UiMessageRequest result = dialogs.Messages.Single();
            Assert.AreEqual(Resources.Duplicate_batch_organize_title, result.Caption, "通知は結果の案内だけです。");
            Assert.AreEqual(string.Format(Resources.Duplicate_batch_organize_result_format, 2, 2, 0, 0, 0, 0), result.MessageBoxText);
            Assert.IsTrue(composition.OperationAdmission.TryEnter(out IDisposable? next), "受付を解放します。");
            next.Dispose();
        }, dialogService: dialogs);
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

        /// <summary>案内を記録する先です。null（既定）のケースでは、案内を出すこと自体が失敗です。</summary>
        internal List<UiMessageRequest>? Messages { get; set; }

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            if (Messages == null) { throw new AssertFailedException("案内は出しません。"); }
            Messages.Add(request);
            return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        /// <summary>分裂配置の探索の進捗は、実際の背景実行で動かします。</summary>
        private readonly DuplicateSupport.BackgroundProgressRunner progress = new();

        public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default)
            => progress.RunAsync(request, operation);
    }
}
