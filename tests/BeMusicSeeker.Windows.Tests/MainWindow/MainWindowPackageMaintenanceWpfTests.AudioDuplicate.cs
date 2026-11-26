using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.Tests.TreeSelectionDisplayTestSupport;
using static BeMusicSeeker.TestSupport.Components.SelectedChartAudioConversionWorkflowOwnerTestSupport;

namespace BeMusicSeeker.Tests;

public sealed partial class MainWindowPackageMaintenanceWpfTests
{
    /// <summary>
    /// 同じフォルダに同名のWAVとOGGを作ります。探索の列挙は、各ケースが管理側の列挙（<c>rootFileEnumerator</c>）をフィクスチャのライブラリへ渡して固定します。
    /// </summary>
    private static void CreateAudioDuplicatePair(string folder)
    {
        File.WriteAllBytes(Path.Combine(folder, "x.wav"), []);
        File.WriteAllBytes(Path.Combine(folder, "x.ogg"), []);
    }

    /// <summary>
    /// 保証：コンパイル済みのメンテナンスツリーで、重複音源のルートは分裂配置のルートの次にあり、見出しはリソースの文言である。ルートの選択が探索の進捗（所有者はメインウィンドウ）を経て、
    /// フォルダの譜面を一覧へ表示し、フォルダ項目は「エクスプローラーで開く」だけを持つ（統合先メニューも Ctrl+G もない）。
    /// 結果が失効した後に、受付が使用中のとき選び直すと、Busy案内を一件だけ出し、進捗を表示せず、表示中の一覧のインスタンスも変えない。
    /// 検出する誤り：ルートの未接続・位置・見出しの取り違え、進捗の所有者の欠落、重複用のメニュー・ショートカットの混入、Busy時の一覧の置換・進捗の表示。
    /// </summary>
    [TestMethod]
    public void AudioDuplicateTree_RootFollowsSplitPlacementAndRunsProgressUnderMainWindow()
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
            string folderPath = charts[0].Directory;
            CreateAudioDuplicatePair(folderPath);
            var root = (TreeViewItem)window.FindName("treeViewItemSearchAudioDuplicate");
            var split = (TreeViewItem)window.FindName("treeViewItemSearchSplitPlacement");
            Assert.AreEqual(Resources.Search_audio_duplicate, root.Header);
            var parent = ItemsControl.ItemsControlFromItemContainer(root);
            Assert.AreEqual(parent.Items.IndexOf(split) + 1, parent.Items.IndexOf(root), "分裂配置チェックの次に置く。");
            Assert.AreEqual(0, root.Items.Count, "探索前は子を持たない。");

            Task handler = MainWindowEventCompletion.CaptureAsyncEventCompletion(() => root.IsSelected = true);
            TestUiDispatcherHost.AwaitTaskOnDispatcher(handler, "audio duplicate root selection handler");
            TestUiDispatcherHost.ProcessQueuedPresentation();

            Assert.IsNotNull(dialogs.ProgressRequest, "探索の進捗を表示する。");
            Assert.AreSame(window, dialogs.ProgressRequest.Owner, "進捗画面の所有者はメインウィンドウ。");
            Assert.AreEqual(Resources.Audio_duplicate_search_progress_title, dialogs.ProgressRequest.Title);
            Assert.IsTrue(dialogs.ProgressRequest.Settings.ShowProgressBarIndeterminate);
            CollectionAssert.AreEquivalent(
                charts.Where(chart => string.Equals(chart.Directory, folderPath, StringComparison.OrdinalIgnoreCase)).Select(chart => chart.Path).ToArray(),
                viewModel.MainChartList.Rows.Cast<LibraryChartRow>().Select(row => row.Chart.Path).ToArray(), "そのフォルダの譜面だけを表示する。");
            Assert.AreEqual(2, root.Items.Count, "整理候補一覧のノードとグループ。");
            DuplicateGroup group = library.AudioDuplicateFolders!.Single().Group;
            TreeViewItem folderItem = RealizeDuplicateFolderItem(root, group, library.AudioDuplicateFolders!.Single().Folder);
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
            TestUiDispatcherHost.AwaitTaskOnDispatcher(emptied.Task, "audio duplicate invalidation empties list");
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.IsNull(library.AudioDuplicateFolders);
            System.Collections.IList rows = viewModel.MainChartList.Rows;
            UiProgressRequestHolder before = new(dialogs.ProgressRequest);
            Assert.IsTrue(library.OperationAdmission.TryEnter(out IDisposable? other));
            try
            {
                root.IsSelected = false;
                Task busyHandler = MainWindowEventCompletion.CaptureAsyncEventCompletion(() => root.IsSelected = true);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(busyHandler, "audio duplicate busy selection handler");
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.AreEqual(1, dialogs.Messages.Count, "Busy案内は一件。");
                Assert.AreEqual(Resources.Warn_LibraryOperationBusy, dialogs.Messages[0].MessageBoxText);
                Assert.AreSame(before.Request, dialogs.ProgressRequest, "進捗を表示しない。");
                Assert.AreSame(rows, viewModel.MainChartList.Rows, "表示中の一覧を変えない。");
            }
            finally { other!.Dispose(); }
        }, terminal, dialogService: dialogs, rootFileEnumerator: new BeMusicSeeker.Models.Utils.FastRootFileEnumerator());
    }

    /// <summary>
    /// 保証：整理候補一覧のノードの選択で、専用の表（KEEPの列がなくCONFLICTSの列があり、統合候補一覧の表は出ない）と、一行の操作の行が表示され、
    /// 操作の行は「衝突整理」のコンボボックス（既定は「OGGを残す」）、「表示中をすべて選択」「表示中をすべて解除」「選択フォルダを確認」「選択フォルダを整理」を持つ。
    /// 優先統合先・簡易チェック・同等性検査のチェック・分裂配置の探索条件の行は出ない。ノードの選択は結果が現在のものでなければ探索する。
    /// 検出する誤り：統合候補一覧の表・操作の行の流用、KEEP列の混入、CONFLICTS列の欠落、重複用の行の混入、操作の既定値の誤り。
    /// </summary>
    [TestMethod]
    public void AudioDuplicateTree_CandidateListNodeShowsItsOwnTableAndOperationRow()
    {
        var dialogs = new RecordingDialogService(new EventLog());
        RunDuplicateSelectionFixture((scope, viewModel, window, library, charts, navigation) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            CreateAudioDuplicatePair(charts[0].Directory);
            var root = (TreeViewItem)window.FindName("treeViewItemSearchAudioDuplicate");
            Task rootHandler = MainWindowEventCompletion.CaptureAsyncEventCompletion(() => root.IsSelected = true);
            TestUiDispatcherHost.AwaitTaskOnDispatcher(rootHandler, "audio duplicate root selection handler");
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.IsInstanceOfType<DuplicateMergeCandidateListNode>(root.Items[0]);

            var node = (TreeViewItem)root.ItemContainerGenerator.ContainerFromIndex(0);
            Task nodeHandler = MainWindowEventCompletion.CaptureAsyncEventCompletion(() => node.IsSelected = true);
            TestUiDispatcherHost.AwaitTaskOnDispatcher(nodeHandler, "audio duplicate candidate list node selection handler");
            CandidateListAwaitSupport.AwaitActive(viewModel.AudioDuplicateCandidateList, "audio duplicate candidate list adoption");
            TestUiDispatcherHost.ProcessQueuedPresentation();

            Assert.IsTrue(viewModel.AudioDuplicateCandidateList.IsViewActive);
            Assert.AreSame(viewModel.AudioDuplicateCandidateList, viewModel.DisplayedCandidatePresentation);
            var table = (CustomTableView)window.FindName("customTableAudioDuplicateCandidates");
            var mergeTable = (CustomTableView)window.FindName("customTableDuplicateCandidates");
            Assert.AreEqual(Visibility.Visible, table.Visibility);
            Assert.AreEqual(Visibility.Collapsed, mergeTable.Visibility, "統合候補一覧の表は出さない。");
            CollectionAssert.AreEqual(
                new[] { "Select", "Status", "Conflicts", "Path", "Folder", "Title", "Artist" }, table.Columns.Select(column => column.Id).ToArray(), "KEEPの列はなく、CONFLICTSの列がある。");
            Assert.AreEqual(1, viewModel.AudioDuplicateCandidateList.Rows.Count);

            var bar = (Border)window.FindName("audioDuplicateCandidateOperationBar");
            Assert.AreEqual(Visibility.Visible, bar.Visibility);
            Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)window.FindName("duplicateCandidateOperationBar")).Visibility, "重複用の操作の行は出さない。");
            var panel = (StackPanel)bar.Child;
            string[] ids = [.. panel.Children.OfType<FrameworkElement>().Select(element => System.Windows.Automation.AutomationProperties.GetAutomationId(element))];
            CollectionAssert.AreEqual(
                new[] { string.Empty, "AudioDuplicateCandidateCollisionCleanup", "AudioDuplicateCandidateSelectVisible", "AudioDuplicateCandidateClearVisible", "AudioDuplicateCandidateVerify", "AudioDuplicateCandidateOrganize" },
                ids, "操作の行は一行で、衝突整理・選択・解除・確認・整理の順。");
            var combo = (ComboBox)window.FindName("audioDuplicateCandidateCollisionCleanupComboBox");
            Assert.AreEqual(AudioDuplicateCollisionCleanupMode.KeepOgg, combo.SelectedValue, "既定は「OGGを残す」。");
            CollectionAssert.AreEqual(
                new[] { Resources.Duplicate_merge_candidate_collision_cleanup_keep_ogg, Resources.Duplicate_merge_candidate_collision_cleanup_keep_wav },
                combo.Items.OfType<ComboBoxItem>().Select(item => (string)item.Content).ToArray());
            Assert.AreEqual(Resources.Audio_duplicate_candidate_verify_selected, ((Button)window.FindName("audioDuplicateCandidateVerifyButton")).Content);
            Assert.AreEqual(Resources.Audio_duplicate_organize_selected, ((Button)window.FindName("audioDuplicateCandidateOrganizeButton")).Content);
            Assert.AreEqual(0, new[] { "duplicateCandidateAudioEquivalenceRow", "splitPlacementCriteriaRow" }
                .Count(name => ((FrameworkElement)window.FindName(name)).IsVisible), "同等性検査・探索条件の行は出さない。");
        }, dialogService: dialogs, rootFileEnumerator: new BeMusicSeeker.Models.Utils.FastRootFileEnumerator());
    }

    /// <summary>同名のWAVとOGGを、実際に復号して同等と判定できる内容で作ります。</summary>
    private static (string Wav, string Ogg) CreateEquivalentAudioPair(TestBmsLibrary library, string folder)
    {
        string audio = Path.Combine(AppContext.BaseDirectory, "TestData", "audio");
        string wav = Path.Combine(folder, "x.wav"), ogg = Path.Combine(folder, "x.ogg");
        BeMusicSeeker.TestSupport.Components.DuplicateMergeAudioTestFiles.WriteWave(
            wav, 44100, 1, BeMusicSeeker.TestSupport.Components.DuplicateMergeAudioTestFiles.ReadFloat32(File.ReadAllBytes(Path.Combine(audio, "nvorbis-1test-reference.f32"))));
        File.Copy(Path.Combine(audio, "nvorbis-1test.ogg"), ogg);
        return (wav, ogg);
    }

    /// <summary>重複音源のルートを選んで探索し、整理候補一覧のノードを選んで、一覧の採用（表示中への遷移）まで待ちます。</summary>
    private static TreeViewItem SelectAudioDuplicateCandidateListNode(MainWindowViewModel viewModel, TreeViewItem root)
    {
        AwaitHandler(() => root.IsSelected = true, "audio duplicate root selection");
        TestUiDispatcherHost.ProcessQueuedPresentation();
        TreeViewItem node = RealizeCandidateListNode(root);
        AwaitHandler(() => node.IsSelected = true, "audio duplicate candidate list node selection");
        CandidateListAwaitSupport.AwaitActive(viewModel.AudioDuplicateCandidateList, "audio duplicate candidate list adoption");
        TestUiDispatcherHost.ProcessQueuedPresentation();
        Assert.IsTrue(viewModel.AudioDuplicateCandidateList.IsViewActive);
        return node;
    }

    /// <summary>
    /// 保証：実画面のボタンから、実際のWAV/OGG（同等な内容）を、実ワークフロー・実際の検査器・実ライブラリで、確認→STATUS→整理の順に通す。
    /// 確認でSTATUSが「安全（WAV/OGG同等）」になりSELECTがオフになる。整理は確認ダイアログを経て、削除の窓口の代替（実ファイルを消す記録つきの窓口）へ捨てる形式（OGGを残すので.wav）だけを送り、
    /// 衝突名が残らないフォルダは結果から消えて、一覧は行0件・ノードが消えた状態へ作り直される。結果の案内は確認・整理で一回ずつ。
    /// 検出する誤り：実ボタンとワークフローの未接続、STATUSの未反映、捨てる形式の取り違え、整理後の一覧の作り直しの欠落・ノードの残存。
    /// </summary>
    [TestMethod]
    public void AudioDuplicateCandidateList_RealButtonsVerifyThenOrganizeEquivalentPair()
    {
        var dialogs = new RecordingDialogService(new EventLog());
        var mutations = new OwnedChartCollectionTestSupport.TestFileMutationService();
        var deleted = new List<string>();
        mutations.BeforeFileDelete = deleted.Add;
        RunDuplicateSelectionFixture((scope, viewModel, window, library, charts, navigation) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            (string wav, string ogg) = CreateEquivalentAudioPair(library, charts[0].Directory);
            var root = (TreeViewItem)window.FindName("treeViewItemSearchAudioDuplicate");
            AudioDuplicateCandidateListOwner list = viewModel.AudioDuplicateCandidateList;
            SelectAudioDuplicateCandidateListNode(viewModel, root);
            Assert.AreEqual(1, list.RowSet.Count);

            ClickButton(window, "audioDuplicateCandidateSelectVisibleButton");
            Assert.AreEqual(1, list.SelectedGroupCount);
            AwaitHandler(() => ClickButton(window, "audioDuplicateCandidateVerifyButton"), "audio duplicate verify toolbar");
            TestUiDispatcherHost.ProcessQueuedPresentation();

            string equivalent = string.Format(Resources.Duplicate_merge_candidate_status_note_format, Resources.Duplicate_merge_candidate_status_safe, Resources.Duplicate_merge_candidate_status_audio_equivalent);
            Assert.AreEqual(equivalent, list.RowSet[0].StatusText, "実際の復号で同等と判定する");
            Assert.AreEqual(0, list.SelectedGroupCount, "確認でSELECTを消費する");
            Assert.AreEqual(string.Format(Resources.Audio_duplicate_verify_result_format, 1, 1, 0, 0), dialogs.Messages.Last().MessageBoxText);
            Assert.IsTrue(File.Exists(wav) && File.Exists(ogg), "確認はファイルを変えない");

            int messagesBeforeOrganize = dialogs.Messages.Count;
            ClickButton(window, "audioDuplicateCandidateSelectVisibleButton");
            AwaitHandler(() => ClickButton(window, "audioDuplicateCandidateOrganizeButton"), "audio duplicate organize toolbar");
            CandidateListAwaitSupport.AwaitRows(list, 0);
            CandidateListAwaitSupport.AwaitCount(root.Items, 0, "audio duplicate tree children removed");
            TestUiDispatcherHost.ProcessQueuedPresentation();

            Assert.IsNotNull(dialogs.ConfirmRequest, "確認ダイアログを経る");
            Assert.IsFalse(File.Exists(wav), "OGGを残し、WAVを捨てる");
            Assert.IsTrue(File.Exists(ogg));
            Assert.AreEqual(1, deleted.Count);
            Assert.AreEqual(1, dialogs.Messages.Count - messagesBeforeOrganize, "整理の結果の案内は一回");
            StringAssert.StartsWith(dialogs.Messages.Last().MessageBoxText, string.Format(Resources.Audio_duplicate_organize_result_format, 1, 1, 0, 0, 0));
            Assert.AreEqual(0, library.AudioDuplicateFolders!.Count, "衝突名が残らないフォルダは結果から消える");
            Assert.AreEqual(0, list.RowSet.Count, "一覧は行0件へ作り直される");
            Assert.IsTrue(library.OperationAdmission.CanEnter, "受付を解放している");
        }, dialogService: dialogs, fileMutations: mutations, rootFileEnumerator: new BeMusicSeeker.Models.Utils.FastRootFileEnumerator());
    }

    private static void InvokeAudioRowActivated(MainWindow window, CustomTableView table, AudioDuplicateCandidateRow row)
    {
        CustomTableHitTestResult hit = new(CustomTableHitKind.Cell, row.Index, row, table.Columns.First(column => column.Id == "Path"), 3, Rect.Empty);
        var args = new CustomTableRowRequestedEventArgs(hit, openAtMousePosition: true);
        typeof(MainWindow).GetMethod("customTableAudioDuplicateCandidates_RowActivated", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, [table, args]);
    }

    /// <summary>
    /// 保証：整理候補一覧の行のダブルクリック（有効なSELECT以外のセル）とEnterは、重複音源のツリーの対応するグループを展開してフォルダノードを選び、そのフォルダの譜面一覧へ移る。
    /// 有効なSELECTのダブルクリックは遷移しない。検出する誤り：重複・分裂配置のツリーのノードの選択、グループ全体の選択、遷移しない行、SELECTのダブルクリックでの遷移。
    /// </summary>
    [TestMethod]
    public void AudioDuplicateCandidateList_RowActivationSelectsTheFolderNodeInTheAudioDuplicateTree()
    {
        var dialogs = new RecordingDialogService(new EventLog());
        RunDuplicateSelectionFixture((scope, viewModel, window, library, charts, navigation) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            CreateAudioDuplicatePair(charts[0].Directory);
            var root = (TreeViewItem)window.FindName("treeViewItemSearchAudioDuplicate");
            var table = (CustomTableView)window.FindName("customTableAudioDuplicateCandidates");
            AudioDuplicateCandidateListOwner list = viewModel.AudioDuplicateCandidateList;
            SelectAudioDuplicateCandidateListNode(viewModel, root);
            AudioDuplicateCandidateRow target = list.RowSet[0];
            string[] expected = [.. charts.Where(chart => string.Equals(chart.Directory, target.Path, StringComparison.OrdinalIgnoreCase)).Select(chart => chart.Path)];

            // 有効なSELECTのセルのダブルクリックは遷移しない。
            CustomTableHitTestResult selectHit = HitCell(table, target.Index, "Select", out Point selectPoint);
            Assert.IsTrue(table.HandleCellMouseDown(selectHit, MouseButton.Left, ModifierKeys.None, 1, selectPoint));
            Assert.IsTrue(table.HandleCellMouseDown(selectHit, MouseButton.Left, ModifierKeys.None, 2, selectPoint));
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.IsTrue(list.IsViewActive, "SELECTのダブルクリックは遷移しない");

            // Enter（行の活性化）：フォルダノードを選び、そのフォルダの譜面一覧へ移る。
            table.SelectedIndex = target.Index;
            AwaitHandler(() => InvokeAudioRowActivated(window, table, target), "audio duplicate row Enter");
            AwaitMainRows(viewModel, window, expected);
            TestUiDispatcherHost.ProcessQueuedPresentation();
            DuplicateGroup group = target.State.Group;
            var groupItem = (TreeViewItem)root.ItemContainerGenerator.ContainerFromItem(group);
            Assert.IsNotNull(groupItem, "対応するグループの項目");
            Assert.IsTrue(groupItem.IsExpanded, "グループを展開する");
            TreeViewItem folderItem = FindDescendants<TreeViewItem>(groupItem)
                .Single(item => item.DataContext is string path && string.Equals(path, target.Path, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(folderItem.IsSelected, "フォルダノードを選ぶ");
            Assert.IsFalse(groupItem.IsSelected, "グループ全体は選ばない");
            Assert.IsFalse(list.IsViewActive, "フォルダの譜面一覧へ移り、専用の表を隠す");
            Assert.AreEqual(Visibility.Visible, ((CustomTableView)window.FindName("customTableView")).Visibility);
        }, dialogService: dialogs, rootFileEnumerator: new BeMusicSeeker.Models.Utils.FastRootFileEnumerator());
    }

    private static void RaiseAudioDuplicateRootClick(TreeViewItem root)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Source = root };
        Task handler = MainWindowEventCompletion.CaptureAsyncEventCompletion(() => root.RaiseEvent(args));
        TestUiDispatcherHost.AwaitTaskOnDispatcher(handler, "audio duplicate root click handler");
        TestUiDispatcherHost.ProcessQueuedPresentation();
    }

    private static void RaiseAudioDuplicateRootEnter(TreeViewItem root)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(root), 0, Key.Enter)
        { RoutedEvent = UIElement.KeyDownEvent, Source = root };
        Task handler = MainWindowEventCompletion.CaptureAsyncEventCompletion(() => root.RaiseEvent(args));
        TestUiDispatcherHost.AwaitTaskOnDispatcher(handler, "audio duplicate root Enter handler");
        TestUiDispatcherHost.ProcessQueuedPresentation();
    }

    /// <summary>
    /// 保証：選択済みのルートのクリックとEnterキーは、有効な結果があれば探索も進捗も案内もせず何もしない。結果が失効して一覧が空になった後は、どちらも探索を始める（進捗を新たに出す）。
    /// 検出する誤り：有効な結果での再探索、失効後の再操作が無視される、クリックとEnterの片方だけの接続。
    /// </summary>
    [TestMethod]
    public void AudioDuplicateTree_SelectedRootClickAndEnterSearchOnlyWithoutCurrentResult()
    {
        var dialogs = new RecordingDialogService(new EventLog());
        RunDuplicateSelectionFixture((scope, viewModel, window, library, charts, navigation) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            CreateAudioDuplicatePair(charts[0].Directory);
            var root = (TreeViewItem)window.FindName("treeViewItemSearchAudioDuplicate");
            AwaitHandler(() => root.IsSelected = true, "audio duplicate root selection");
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.IsTrue(library.HasCurrentAudioDuplicateFolders);
            UiProgressRequest? first = dialogs.ProgressRequest;
            int messages = dialogs.Messages.Count;

            RaiseAudioDuplicateRootClick(root);
            RaiseAudioDuplicateRootEnter(root);
            Assert.AreSame(first, dialogs.ProgressRequest, "有効な結果があれば探索しない");
            Assert.AreEqual(messages, dialogs.Messages.Count);

            InvalidateSplitPlacementAndSettle(viewModel, () => library.BmsCharts = charts);
            Assert.IsNull(library.AudioDuplicateFolders);
            Assert.IsTrue(root.IsSelected);
            RaiseAudioDuplicateRootClick(root);
            Assert.IsTrue(library.HasCurrentAudioDuplicateFolders, "クリックで探索する");
            UiProgressRequest? second = dialogs.ProgressRequest;
            Assert.AreNotSame(first, second);

            InvalidateSplitPlacementAndSettle(viewModel, () => library.BmsCharts = charts);
            Assert.IsNull(library.AudioDuplicateFolders);
            RaiseAudioDuplicateRootEnter(root);
            Assert.IsTrue(library.HasCurrentAudioDuplicateFolders, "Enterキーで探索する");
            Assert.AreNotSame(second, dialogs.ProgressRequest);
            Assert.AreEqual(messages, dialogs.Messages.Count, "案内は出ない");
        }, dialogService: dialogs, rootFileEnumerator: new BeMusicSeeker.Models.Utils.FastRootFileEnumerator());
    }
}
