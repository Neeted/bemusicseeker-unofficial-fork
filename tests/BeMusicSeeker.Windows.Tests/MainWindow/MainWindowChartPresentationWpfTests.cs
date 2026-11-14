using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Tests;


/// <remarks>compiled列menuと検索・編集Controlのイベント接続、popupの実配置・仮想化・IME経路を検査するため実WPFを使います。候補生成・保存失敗・編集可否の条件網羅はSearchとCustomTableのownerテストが分担します。</remarks>
[TestClass]
[TestCategory("Windows.Shell")]
// MainWindowのApplication.Resourcesとテーマ設定を一時変更し、所有Window・popupを閉じてから共有状態を復元します。
[DoNotParallelize]
public sealed class MainWindowChartPresentationWpfTests
{
    /// <summary>compiled表の安定ID・表示種別/言語に応じた既存表示名・patternと編集可否を既存ownerへ接続し、readonly照会が編集開始・確定を発行しないことを確認します。</summary>
    [TestMethod]
    public void CompiledTables_ExposeAutomationPatternsAndPureOwnerEditAvailability()
    {
        WithSimpleTextBoxStyles(() => MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            TestSettingsFactory.CreateIsolatedSettings(), (scope, viewModel, window) =>
            {
                window.Width = 1000d; window.Height = 700d;
                MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
                var table = (CustomTableView)window.FindName("customTableView");
                var summary = (CustomTableView)window.FindName("customTablePlaylistSummary");
                AutomationPeer root = UIElementAutomationPeer.CreatePeerForElement(table) ?? throw new AssertFailedException("譜面一覧peerがありません。");
                AutomationPeer summaryPeer = UIElementAutomationPeer.CreatePeerForElement(summary) ?? throw new AssertFailedException("概要一覧peerがありません。");
                Assert.AreEqual("MainChartTable", root.GetAutomationId());
                Assert.AreEqual(Resources.Library, root.GetName());
                Assert.AreEqual("PlaylistSummaryTable", summaryPeer.GetAutomationId());
                Assert.AreEqual(Resources.Playlist, summaryPeer.GetName());
                foreach (AutomationPeer peer in new[] { root, summaryPeer })
                {
                    Assert.IsInstanceOfType<ISelectionProvider>(peer.GetPattern(PatternInterface.Selection));
                    Assert.IsInstanceOfType<IScrollProvider>(peer.GetPattern(PatternInterface.Scroll));
                    Assert.IsInstanceOfType<IItemContainerProvider>(peer.GetPattern(PatternInterface.ItemContainer));
                    Assert.IsNull(peer.GetPattern(PatternInterface.Grid));
                    Assert.IsNull(peer.GetPattern(PatternInterface.Table));
                }
                CultureInfo? previousCulture = Resources.Culture;
                bool previousDetailActive = viewModel.PlaylistWorkspace.IsPlaylistDetailViewActive;
                try
                {
                    ResourceService.Current.ChangeCulture("ja-JP");
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreEqual(Resources.Library, root.GetName());
                    viewModel.PlaylistWorkspace.IsPlaylistDetailViewActive = true;
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreEqual(Resources.Playlist, root.GetName());
                    viewModel.PlaylistWorkspace.IsPlaylistDetailViewActive = false;
                    viewModel.PlayHistory.BeginRequest(PlayHistoryPeriodRequest.All(), string.Empty,
                        viewModel.PlayHistory.SelectedDisplayTargetIdentity, viewModel.PlayHistory.DisplayTargetRevision);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreEqual(Resources.Play_history_tree_root, root.GetName());
                    ResourceService.Current.ChangeCulture("en-US");
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreEqual(Resources.Play_history_tree_root, root.GetName());
                    Assert.AreEqual(Resources.Playlist, summaryPeer.GetName());
                    viewModel.PlayHistory.Deactivate();
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreEqual(Resources.Library, root.GetName());
                    Assert.AreEqual("MainChartTable", root.GetAutomationId());
                }
                finally
                {
                    viewModel.PlayHistory.Deactivate();
                    viewModel.PlaylistWorkspace.IsPlaylistDetailViewActive = previousDetailActive;
                    ResourceService.Current.ChangeCulture(previousCulture?.Name ?? "en-US");
                    Resources.Culture = previousCulture;
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                }
                var entry = PackageChartEntry.FromChart(ChartTestValues.Empty() with { Path = @"C:\uia-compiled\chart.bms" });
                var row = LibraryChartRow.FromPackageChartEntry(entry);
                ChartListSourceRow[] sources = [.. ChartListSourceRow.BuildPackageRows([entry])];
                table.ItemsSource = new ChartListVirtualView(sources, ChartListOrder.CreateTitleAscending(sources), _ => row, selectionScope: "compiled-uia");
                var settings = new CustomTableColumnSettings(); settings.InstallDst.Visibility = Visibility.Visible;
                table.Columns = [.. CustomTableColumnFactory.CreateMainColumns(settings).Where(column => column.Id == "InstallDst")];
                viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.PendingInstallFolderSelected);
                MaterializeTable(table, 900d, 220d);
                int started = 0, completed = 0;
                viewModel.MainChartList.CellEditStarted += (_, _) => started++;
                viewModel.MainChartList.CellEditEndedRequested += (_, _) => completed++;
                AutomationPeer cell = root.GetChildren().Single(child => child.GetAutomationControlType() == AutomationControlType.ListItem).GetChildren().Single();
                var value = (IValueProvider)cell.GetPattern(PatternInterface.Value);
                Assert.IsFalse(value.IsReadOnly);
                viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.PlayHistorySelected);
                Assert.IsTrue(value.IsReadOnly);
                Assert.AreEqual(0, started); Assert.AreEqual(0, completed);
                table.ItemsSource = null;
            }));
    }

    /// <summary>譜面一覧の並びに非依存の実score変更ではResetを送らず、同じ実editorと未確定文字列を保ち、選択復元が編集確定を引き起こす誤りを検出します。</summary>
    [TestMethod]
    public void ChartScoreValueChange_KeepsActiveEditorAndUncommittedText()
    {
        RegularChartListOwnerTestSupport.WithTemporarySongDb(path => TestUiDispatcherHost.RunWindowTest(scope =>
        {
            using var fixture = new RegularScoreDeliveryTestFixture(path);
            Assert.IsTrue(fixture.Owner.TryApplyVirtualNormalLibrary(new()
            {
                Library = fixture.Library,
                ModeFilter = ChartModeFilter.All,
                SortColumnName = nameof(LibraryChartRow.Title),
                SortDirection = System.ComponentModel.ListSortDirection.Ascending,
                ColumnSelection = new(new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD), false, 0,
                    MainViewUpdateMode.FullScanAllChartsFilterSelected, Visibility.Collapsed, new PlaylistSummaryColumnSettings()),
                Mode = MainViewUpdateMode.FullScanAllChartsFilterSelected,
                Stopwatch = System.Diagnostics.Stopwatch.StartNew()
            }).WasCommitted);
            var column = new CustomTableColumn("Title", "Title",
                new CustomTableColumnSettings.ColumnLayout { Width = 240, Visibility = Visibility.Visible },
                fallbackOrder: 0, sortMemberPath: null, alignment: TextAlignment.Left,
                textSelector: row => ((LibraryChartRow)row).Title, editPropertyName: "Title");
            var table = new CustomTableView
            {
                Width = 300,
                Height = 150,
                HeaderHeight = 0,
                RowHeight = 45,
                Columns = [column],
                ItemsSource = fixture.View,
                SelectedIndex = 0
            };
            var window = new Window { Width = 320, Height = 200, Content = table, ShowInTaskbar = false, ShowActivated = false };
            scope.ShowAndWaitForContentRendered(window);
            MaterializeTable(table, 300, 150);
            int ended = 0;
            table.CellEditEnded += (_, _) => ended++;
            Assert.IsTrue(table.HandleKeyDown(Key.Right, ModifierKeys.None));
            Assert.IsTrue(table.HandleKeyDown(Key.F2, ModifierKeys.None));
            TextBox editor = GetInstalledEditor(table) ?? throw new AssertFailedException("実editorへ到達しませんでした。");
            editor.Text = "未確定 入力";
            Task delivery = Task.CompletedTask;
            try
            {
                fixture.Change(2, 40);
                delivery = fixture.Owner.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None);
                Task<ScheduledScoreOperation> next = fixture.Scheduler.NextAsync();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(next, "editor score callback");
                ScheduledScoreOperation operation = next.GetAwaiter().GetResult();
                operation.Run();
                operation.Complete();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(delivery, "editor score completion");
                Assert.AreSame(editor, GetInstalledEditor(table));
                Assert.AreEqual("未確定 入力", editor.Text);
                Assert.AreEqual(0, ended);
                Assert.AreEqual(0, table.SelectedIndex);
            }
            finally
            {
                fixture.Scheduler.AbortOutstanding();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(delivery, "editor score cleanup");
                table.HandleKeyDown(Key.Escape, ModifierKeys.None);
                table.ItemsSource = null;
            }
        }));
    }

    private static void WithSimpleTextBoxStyles(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        TestUiDispatcherHost.Invoke(() =>
        {
            Application application = Application.Current
                ?? throw new InvalidOperationException("The shared WPF application host is unavailable.");
            if (application.TryFindResource("SimpleTextBox") is Style)
            {
                action();
                return;
            }

            var styles = new ResourceDictionary
            {
                Source = new Uri(
                    "/BeMusicSeeker;component/Themes/Simple Styles.xaml",
                    UriKind.RelativeOrAbsolute)
            };
            application.Resources.MergedDictionaries.Insert(0, styles);
            try
            {
                action();
            }
            finally
            {
                application.Resources.MergedDictionaries.Remove(styles);
            }
        });
    }

    /// <summary>
    /// compiled列メニューのリセットClickが、既定の端末を通して構成の共通の窓口へ確認をちょうど1件送り、明示的なOKのときだけ列の表示を初期化することを確認します。
    /// 取消は初期化せず、表示の失敗（Failed、窓口の例外）は承認とも取消とも読み替えず初期化しません。失敗の案内は、所有する入口が
    /// プレイリストの窓口へMsg_error_unexpectedを1件だけ出し、例外はハンドラーの外へ出しません。
    /// 検出する誤り：確認の宛先や内容・Ownerの取り違え、表示失敗の承認扱い、案内の欠落や重複、例外の流出。
    /// </summary>
    [TestMethod]
    [DataRow("ok")]
    [DataRow("cancel")]
    [DataRow("failed")]
    [DataRow("faulted")]
    public void MainColumnResetClickConfirmsThroughInjectedDialogAndResetsOnlyOnExplicitOk(string scenario)
    {
        bool expectReset = scenario == "ok";
        bool expectFailureNotice = scenario is "failed" or "faulted";
        var presentationFailure = new InvalidOperationException("column reset confirmation presentation failure");
        var rootDialogs = new RecordingRootDialogService
        {
            ConfirmationResult = scenario switch
            {
                "ok" => UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK),
                "cancel" => UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel),
                _ => UiDialogResult.Failed(presentationFailure),
            },
        };
        if (scenario == "faulted")
        {
            rootDialogs.ConfirmationObservedAsync = () => Task.FromException(presentationFailure);
        }
        IUiDialogService playlistDialogs = expectFailureNotice ? new RecordingRootDialogService() : UndeclaredDialogs.Create();

        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                TestSettingsFactory.CreateIsolatedSettings(),
                (viewModel, window) =>
                {
                    viewModel.RegularChartList.InitializeColumnPresentation(MainViewUpdateMode.FolderFilterSelected);
                    CustomTableColumnSettings before = viewModel.MainChartList.ColumnsSettings;
                    var menu = (ContextMenu)window.FindResource("tableColumnHeaderContextMenu");
                    MenuItem reset = menu.Items.OfType<MenuItem>().Last();

                    Task completion = MainWindowEventCompletion.CaptureAsyncEventCompletion(
                        () => reset.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, reset)));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(completion, "column reset click");

                    Assert.AreEqual(1, rootDialogs.ConfirmationCount);
                    Assert.AreEqual(0, rootDialogs.MessageCount);
                    UiConfirmationRequest confirmation = rootDialogs.ConfirmationRequests.Single();
                    Assert.AreEqual(Resources.Msg_init_column_settings, confirmation.MessageBoxText);
                    Assert.AreEqual(Resources.Confirm, confirmation.Caption);
                    Assert.AreEqual(MessageBoxButton.OKCancel, confirmation.Button);
                    Assert.AreEqual(MessageBoxImage.Question, confirmation.Icon);
                    Assert.AreEqual(MessageBoxResult.Cancel, confirmation.DefaultResult);
                    Assert.AreSame(window, confirmation.Owner);

                    if (expectReset)
                    {
                        Assert.AreNotSame(before, viewModel.MainChartList.ColumnsSettings);
                    }
                    else
                    {
                        Assert.AreSame(before, viewModel.MainChartList.ColumnsSettings);
                    }

                    if (expectFailureNotice)
                    {
                        var playlistRecording = (RecordingRootDialogService)playlistDialogs;
                        Assert.AreEqual(0, playlistRecording.ConfirmationCount);
                        Assert.AreEqual(1, playlistRecording.MessageCount);
                        UiMessageRequest notice = playlistRecording.MessageRequests.Single();
                        Assert.AreEqual(Resources.Msg_error_unexpected, notice.MessageBoxText);
                        Assert.AreEqual(Resources.Error, notice.Caption);
                        Assert.AreEqual(MessageBoxButton.OK, notice.Button);
                        Assert.AreEqual(MessageBoxImage.Hand, notice.Icon);
                        Assert.AreSame(window, notice.Owner);
                    }
                },
                allowStartupUiInteraction: true,
                playlistWorkspaceDialogService: playlistDialogs,
                dialogService: rootDialogs);
        });
    }

    /// <summary>既定端末で実FOLDER編集の原文と確定入力が実入口のVMへ届くことを確認します。未変更の確定が受付や活動を起こさないことはCoreのRegularChartFolderRenameTestsが担当します。</summary>
    [TestMethod]
    public void MainWindowFolderEdit_DefaultTerminalPreservesOriginalAndNoOp()
    {
        WithSimpleTextBoxStyles(() => MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            TestSettingsFactory.CreateIsolatedSettings(), (scope, viewModel, window) =>
            {
                window.Width = 1000; window.Height = 700;
                MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
                var row = LibraryChartRow.FromChartFile(ChartFileProjection.WithPath(ChartTestValues.Empty(), @"C:\Library\Pack😀\chart.bms"));
                var table = (CustomTableView)window.FindName("customTableView");
                table.ItemsSource = new[] { row };
                var settings = new CustomTableColumnSettings();
                table.Columns = [.. CustomTableColumnFactory.CreateMainColumns(settings).Where(column => column.EditPropertyName == nameof(LibraryChartRow.Folder))];
                foreach (CustomTableColumn column in table.Columns) { column.Layout.Width = 220; column.Layout.Visibility = Visibility.Visible; }
                viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.FolderFilterSelected);
                MaterializeTable(table, 900, 220);
                table.SelectRowsByPredicate(_ => true);
                var completed = new List<MainChartListCellEditEndedEventArgs>();
                viewModel.MainChartList.CellEditEndedRequested += (_, args) => completed.Add(args);
                RaiseKey(table, Key.F2); RaiseKey(table, Key.Return);
                Assert.AreEqual(1, completed.Count);
                Assert.AreEqual("Pack😀", completed[0].OriginalText); Assert.AreEqual("Pack😀", completed[0].Text);
                Assert.AreSame(row, completed[0].Context.Row);
                Assert.IsTrue(completed[0].Commit);
            }));
    }

    /// <summary>実列factoryの導入先・playlist Memo・URL空欄をF2/Enterで共通端末へ配送し、URL編集のEscape取消と詳細編集状態の終端を確認します。</summary>
    [TestMethod]
    public void MainWindowPendingCellEditUsesDefaultTerminalAndMainChartOwnerRoute()
    {
        var beginning = new List<MainChartListCellEditContext>();
        var started = new List<MainChartListCellEditContext>();
        var completed = new List<MainChartListCellEditEndedEventArgs>();
        const string expectedText = @"C:\wave6e-cell-edit\destination";

        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
                TestSettingsFactory.CreateIsolatedSettings(),
                (scope, viewModel, window) =>
                {
                    viewModel.MainChartList.CellEditBeginningRequested += (_, request) => beginning.Add(request.Context);
                    viewModel.MainChartList.CellEditStarted += (_, context) => started.Add(context);
                    viewModel.MainChartList.CellEditEndedRequested += (_, request) => completed.Add(request);
                    window.Width = 1000d;
                    window.Height = 700d;
                    MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);

                    ChartFile file = ChartTestValues.Empty() with
                    {
                        Path = @"C:\wave6e-cell-edit\pending\chart.bms",
                        Md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                        RawTitle = "Pending cell edit"
                    };
                    var entry = PackageChartEntry.FromChart((file));
                    var row = LibraryChartRow.FromPackageChartEntry(entry);
                    var table = (CustomTableView)window.FindName("customTableView");
                    table.Width = 900d;
                    table.Height = 220d;
                    table.HeaderHeight = 0d;
                    table.RowHeight = 70d;
                    table.ItemsSource = new List<object> { row };
                    var columnSettings = new CustomTableColumnSettings();
                    columnSettings.InstallDst.Visibility = Visibility.Visible;
                    table.Columns = [.. CustomTableColumnFactory.CreateMainColumns(columnSettings)
                    .Where(candidate => string.Equals(candidate.EditPropertyName, "instl_dst", StringComparison.Ordinal))];
                    table.SelectRowsByPredicate(_ => true);
                    viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.PendingInstallFolderSelected);
                    table.Measure(new Size(table.Width, table.Height));
                    table.Arrange(new Rect(0d, 0d, table.Width, table.Height));
                    table.UpdateLayout();

                    CustomTableColumn column = table.Columns
                        .Single(candidate => string.Equals(candidate.EditPropertyName, "instl_dst", StringComparison.Ordinal));
                    Assert.AreEqual(0, table.SelectedIndex);
                    Assert.IsTrue(table.IsCurrentCell(0, column));
                    RaiseKey(table, Key.F2);
                    Assert.AreEqual(1, beginning.Count);
                    Assert.AreEqual(1, started.Count);
                    Assert.IsNotNull(GetInstalledEditor(table));
                    TextBox editor = GetInstalledEditor(table)!;
                    editor.ApplyTemplate();
                    var editorHost = (ScrollViewer)editor.Template.FindName("PART_ContentHost", editor);
                    Assert.AreEqual(editor.Padding, editorHost.Padding, "セルeditorの入力余白はWPF転送だけで所有する。");
                    editor.Text = expectedText;
                    RaiseKey(table, Key.Return);

                    Assert.AreEqual(1, beginning.Count);
                    Assert.AreEqual(1, started.Count);
                    Assert.AreEqual(1, completed.Count);
                    Assert.AreSame(row, beginning[0].Row);
                    Assert.AreSame(row, started[0].Row);
                    Assert.AreSame(row, completed[0].Context.Row);
                    Assert.AreEqual("instl_dst", beginning[0].PropertyName);
                    Assert.AreEqual("instl_dst", started[0].PropertyName);
                    Assert.AreEqual("instl_dst", completed[0].Context.PropertyName);
                    Assert.AreEqual(expectedText, completed[0].Text);
                    Assert.IsTrue(completed[0].Commit);
                    Assert.AreSame(file.Token, row.Chart.Token);

                    var detailEntry = new BMSTableEntry(file) { memo = "before memo" };
                    var detailTable = new BMSTable { name = "Local detail", entries = [detailEntry] };
                    Assert.AreSame(detailTable, detailEntry.parent);
                    PlaylistDetailRow detailRow = new PlaylistDetailSourceRow(detailEntry, resolvedChart: null).CreateViewRow();
                    table.ItemsSource = new List<object> { detailRow };
                    columnSettings.Memo.Visibility = Visibility.Visible;
                    table.Columns = [.. CustomTableColumnFactory.CreateMainColumns(columnSettings)
                        .Where(candidate => candidate.EditPropertyName == nameof(PlaylistDetailRow.memo))];
                    table.SelectRowsByPredicate(_ => true);
                    viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.PlaylistFilterSelected);
                    MaterializeTable(table, table.Width, table.Height);
                    CustomTableColumn memoColumn = table.Columns.Single();
                    Assert.IsTrue(table.IsCurrentCell(0, memoColumn));
                    RaiseKey(table, Key.F2);
                    Assert.AreEqual(2, beginning.Count);
                    Assert.AreEqual(2, started.Count);
                    Assert.IsTrue(viewModel.PlaylistWorkspace.DetailViewState.Source.IsPlaylistCellEditing);
                    TextBox memoEditor = GetInstalledEditor(table) ?? throw new AssertFailedException("Memo編集controlへ到達しませんでした。");
                    memoEditor.Text = "edited memo";
                    RaiseKey(table, Key.Return);
                    Assert.AreEqual(2, completed.Count);
                    Assert.AreSame(detailRow, beginning[1].Row);
                    Assert.AreSame(detailRow, started[1].Row);
                    Assert.AreSame(detailRow, completed[1].Context.Row);
                    Assert.AreEqual(nameof(PlaylistDetailRow.memo), beginning[1].PropertyName);
                    Assert.AreEqual(nameof(PlaylistDetailRow.memo), started[1].PropertyName);
                    Assert.AreEqual(nameof(PlaylistDetailRow.memo), completed[1].Context.PropertyName);
                    Assert.AreEqual("edited memo", completed[1].Text);
                    Assert.IsTrue(completed[1].Commit);
                    // このconstructorは保存providerを持たず同期終端となる。DB保存は下位の5属性Taskへ分担する。
                    Assert.IsFalse(viewModel.PlaylistWorkspace.DetailViewState.Source.IsPlaylistCellEditing);

                    detailEntry.Url = new Uri("https://example.com/cell-url");
                    detailRow = new PlaylistDetailSourceRow(detailEntry, resolvedChart: null).CreateViewRow();
                    table.ItemsSource = new List<object> { detailRow };
                    columnSettings.Url1.Visibility = Visibility.Visible;
                    table.Columns = [.. CustomTableColumnFactory.CreateMainColumns(columnSettings)
                        .Where(candidate => candidate.EditPropertyName == nameof(PlaylistDetailRow.Url))];
                    table.SelectRowsByPredicate(_ => true);
                    MaterializeTable(table, table.Width, table.Height);
                    RaiseKey(table, Key.F2);
                    TextBox urlEditor = GetInstalledEditor(table) ?? throw new AssertFailedException("URL編集controlへ到達しませんでした。");
                    Assert.AreEqual("https://example.com/cell-url", urlEditor.Text);
                    urlEditor.Text = string.Empty;
                    RaiseKey(table, Key.Escape);
                    Assert.AreEqual(3, completed.Count);
                    Assert.IsFalse(completed[2].Commit);
                    Assert.AreEqual(new Uri("https://example.com/cell-url"), detailEntry.Url);
                    Assert.IsFalse(viewModel.PlaylistWorkspace.DetailViewState.Source.IsPlaylistCellEditing);
                    RaiseKey(table, Key.F2);
                    urlEditor = GetInstalledEditor(table) ?? throw new AssertFailedException("URL再編集controlへ到達しませんでした。");
                    urlEditor.Text = string.Empty;
                    RaiseKey(table, Key.Return);
                    Assert.AreEqual(4, beginning.Count);
                    Assert.AreEqual(4, started.Count);
                    Assert.AreEqual(4, completed.Count);
                    Assert.AreSame(detailRow, completed[3].Context.Row);
                    Assert.AreEqual(nameof(PlaylistDetailRow.Url), completed[3].Context.PropertyName);
                    Assert.AreEqual(string.Empty, completed[3].Text);
                    Assert.IsTrue(completed[3].Commit);
                    Assert.IsFalse(viewModel.PlaylistWorkspace.DetailViewState.Source.IsPlaylistCellEditing);
                });
        });
    }

    /// <summary>行選択後のF2開始通知で編集TextBoxの配置と初期値が既に成立し、editor設置前の開始通知を検出します。</summary>
    [TestMethod]
    public void CustomTableCellEditStartedSeesInstalledEditorAfterKeyboardActivation()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            var layout = new CustomTableColumnSettings.ColumnLayout
            {
                Width = 180,
                Visibility = Visibility.Visible
            };
            var column = new CustomTableColumn(
                "Folder",
                "Folder",
                layout,
                fallbackOrder: 0,
                sortMemberPath: null,
                alignment: TextAlignment.Left,
                textSelector: _ => "initial",
                editPropertyName: "Folder");
            var table = new CustomTableView
            {
                Width = 220,
                Height = 70,
                HeaderHeight = 0d,
                RowHeight = 60d,
                Columns = [column],
                ItemsSource = new List<object> { new object() }
            };
            var host = new Window
            {
                Width = 220d,
                Height = 70d,
                Content = table,
                ShowInTaskbar = false,
                ShowActivated = false
            };
            scope.ShowAndWaitForContentRendered(host);
            MaterializeTable(table, 220d, 70d);
            int beginningCount = 0;
            int startedCount = 0;
            TextBox? editorSeenAtStarted = null;
            table.CellEditBeginning += (_, _) => beginningCount++;
            table.CellEditStarted += (_, _) =>
            {
                startedCount++;
                editorSeenAtStarted = table.Children
                    .OfType<Canvas>()
                    .SelectMany(canvas => canvas.Children.OfType<TextBox>())
                    .SingleOrDefault();
            };

            RaiseKey(table, Key.Down);
            Assert.AreEqual(0, table.SelectedIndex);
            Assert.IsTrue(table.IsCurrentCell(0, column));
            RaiseKey(table, Key.F2);

            Assert.AreEqual(1, beginningCount);
            Assert.AreEqual(1, startedCount);
            Assert.IsNotNull(editorSeenAtStarted);
            Assert.IsTrue(editorSeenAtStarted!.Width > 2d);
            Assert.IsTrue(editorSeenAtStarted!.Height > 2d);
            Assert.AreEqual("initial", editorSeenAtStarted!.Text);

            Assert.IsTrue(table.HandleKeyDown(Key.Escape, ModifierKeys.None));
        });
    }

    /// <summary>編集開始が取消された場合と列幅不足の場合は開始要求だけを通知し、editor設置とStarted通知を実行しません。</summary>
    [TestMethod]
    public void CustomTableCellEditFailuresDoNotRaiseStarted()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            var layout = new CustomTableColumnSettings.ColumnLayout
            {
                Width = 180,
                Visibility = Visibility.Visible
            };
            var column = new CustomTableColumn(
                "Folder",
                "Folder",
                layout,
                fallbackOrder: 0,
                sortMemberPath: null,
                alignment: TextAlignment.Left,
                textSelector: _ => "initial",
                editPropertyName: "Folder");

            CustomTableView cancelled = CreateEditableTable(column, 220d, 70d);
            var host = new Window
            {
                Width = 220d,
                Height = 70d,
                Content = cancelled,
                ShowInTaskbar = false,
                ShowActivated = false
            };
            scope.ShowAndWaitForContentRendered(host);
            MaterializeTable(cancelled, 220d, 70d);
            int cancelledStarted = 0;
            int cancelledBeginning = 0;
            cancelled.CellEditBeginning += (_, args) => args.Cancel = true;
            cancelled.CellEditBeginning += (_, _) => cancelledBeginning++;
            cancelled.CellEditStarted += (_, _) => cancelledStarted++;
            RaiseKey(cancelled, Key.Down);
            Assert.AreEqual(0, cancelled.SelectedIndex);
            Assert.IsTrue(cancelled.IsCurrentCell(0, column));
            RaiseKey(cancelled, Key.F2);
            Assert.AreEqual(1, cancelledBeginning);
            Assert.AreEqual(0, cancelledStarted);
            Assert.IsNull(GetInstalledEditor(cancelled));

            var tooSmallLayout = new CustomTableColumnSettings.ColumnLayout
            {
                Width = 2,
                Visibility = Visibility.Visible
            };
            var tooSmallColumn = new CustomTableColumn(
                "Folder",
                "Folder",
                tooSmallLayout,
                fallbackOrder: 0,
                sortMemberPath: null,
                alignment: TextAlignment.Left,
                textSelector: _ => "initial",
                minWidth: 1,
                editPropertyName: "Folder");
            CustomTableView tooSmall = CreateEditableTable(tooSmallColumn, 220d, 70d);
            host.Content = tooSmall;
            MaterializeTable(tooSmall, 220d, 70d);
            int tooSmallBeginning = 0;
            int tooSmallStarted = 0;
            tooSmall.CellEditBeginning += (_, _) => tooSmallBeginning++;
            tooSmall.CellEditStarted += (_, _) => tooSmallStarted++;
            RaiseKey(tooSmall, Key.Down);
            Assert.AreEqual(0, tooSmall.SelectedIndex);
            Assert.IsTrue(tooSmall.IsCurrentCell(0, tooSmallColumn));
            RaiseKey(tooSmall, Key.F2);
            Assert.AreEqual(1, tooSmallBeginning);
            Assert.AreEqual(0, tooSmallStarted);
            Assert.IsNull(GetInstalledEditor(tooSmall));
        });
    }

    /// <summary>実テーマ切替を表と実バーへ反映し、縦・横・両方向で描画領域を保ち、交点の入力領域を重ねないことを確認します。</summary>
    [TestMethod]
    public void CustomTableScrollBarsUseCompiledThemeAndOverlayContract()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Application application = Application.Current
                ?? throw new InvalidOperationException("The shared WPF application host is unavailable.");
            const string trackBackgroundResourceKey = "ScrollBar.TrackBackgroundBrush";
            string previousTheme = Settings.Default.AppearanceTheme;
            bool hadApplicationTrackBackground = application.Resources.Contains(trackBackgroundResourceKey);
            object? previousApplicationTrackBackground = hadApplicationTrackBackground
                ? application.Resources[trackBackgroundResourceKey]
                : null;
            ResourceDictionary? activeThemeResources = null;
            object? previousThemeTrackBackground = null;
            Window? resourceHostWindow = null;
            const double tableWidth = 240d;
            const double tableHeight = 100d;
            try
            {
                application.Resources.Remove(trackBackgroundResourceKey);
                activeThemeResources = application.Resources.MergedDictionaries
                    .LastOrDefault(dictionary => dictionary.Contains(trackBackgroundResourceKey))
                    ?? throw new InvalidOperationException(
                        "The active theme does not define the scroll-bar track background resource.");
                previousThemeTrackBackground = activeThemeResources[trackBackgroundResourceKey];
                var table = new CustomTableView
                {
                    Width = tableWidth,
                    Height = tableHeight,
                    HeaderHeight = 0d,
                    RowHeight = 20d,
                    Columns = [CreateScrollBarContractColumn(120d)],
                    ItemsSource = new List<object> { new object() }
                };
                var resourceHost = new Grid();
                resourceHost.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri(
                        "/BeMusicSeeker;component/Themes/Simple Styles.xaml",
                        UriKind.RelativeOrAbsolute)
                });
                resourceHost.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri(
                        "/BeMusicSeeker;component/Themes/CanonicalDialogStyles.xaml",
                        UriKind.RelativeOrAbsolute)
                });
                resourceHost.Children.Add(table);
                resourceHostWindow = new Window
                {
                    Width = tableWidth,
                    Height = tableHeight,
                    Content = resourceHost
                };
                ScrollBar verticalScrollBar = table.Children
                    .OfType<ScrollBar>()
                    .Single(scrollBar => scrollBar.Orientation == Orientation.Vertical);
                ScrollBar horizontalScrollBar = table.Children
                    .OfType<ScrollBar>()
                    .Single(scrollBar => scrollBar.Orientation == Orientation.Horizontal);

                object trackBackground = table.FindResource(trackBackgroundResourceKey);
                Assert.AreSame(trackBackground, table.Background);

                resourceHostWindow.Measure(new Size(tableWidth, tableHeight));
                resourceHostWindow.Arrange(new Rect(0d, 0d, tableWidth, tableHeight));
                resourceHostWindow.UpdateLayout();
                MaterializeScrollBarContractTable(table, tableWidth, tableHeight);
                Assert.IsNotNull(verticalScrollBar.Template);
                Assert.IsNotNull(horizontalScrollBar.Template);
                AssertScrollBarPresentation(verticalScrollBar, Orientation.Vertical);
                AssertScrollBarPresentation(horizontalScrollBar, Orientation.Horizontal);
                Assert.AreEqual(Visibility.Collapsed, verticalScrollBar.Visibility);
                Assert.AreEqual(Visibility.Collapsed, horizontalScrollBar.Visibility);

                application.Resources.Remove(trackBackgroundResourceKey);
                string alternateTheme = string.Equals(
                    AppThemeService.NormalizeTheme(previousTheme),
                    AppThemeService.Dark,
                    StringComparison.Ordinal)
                    ? AppThemeService.Light
                    : AppThemeService.Dark;
                Settings.Default.AppearanceTheme = alternateTheme;
                AppThemeService.ApplyTheme(alternateTheme);
                FlushResourceUpdates(table);
                object switchedThemeTrackBackground = table.FindResource(trackBackgroundResourceKey);
                Assert.AreNotSame(trackBackground, switchedThemeTrackBackground);
                Assert.AreSame(switchedThemeTrackBackground, table.Background);

                table.ItemsSource = Enumerable.Range(0, 20).Select(_ => new object()).ToList();
                MaterializeScrollBarContractTable(table, tableWidth, tableHeight);
                Assert.AreEqual(Visibility.Visible, verticalScrollBar.Visibility);
                Assert.AreEqual(Visibility.Collapsed, horizontalScrollBar.Visibility);

                table.ItemsSource = new List<object> { new object() };
                table.Columns = [CreateScrollBarContractColumn(600d)];
                MaterializeScrollBarContractTable(table, tableWidth, tableHeight);
                Assert.AreEqual(Visibility.Collapsed, verticalScrollBar.Visibility);
                Assert.AreEqual(Visibility.Visible, horizontalScrollBar.Visibility);

                table.ItemsSource = Enumerable.Range(0, 20).Select(_ => new object()).ToList();
                MaterializeScrollBarContractTable(table, tableWidth, tableHeight);
                Assert.AreEqual(Visibility.Visible, verticalScrollBar.Visibility);
                Assert.AreEqual(Visibility.Visible, horizontalScrollBar.Visibility);
                Assert.AreEqual(tableWidth, table.SurfaceWidth);
                Assert.AreEqual(tableHeight, table.SurfaceHeight);
                Rect verticalBounds = verticalScrollBar.TransformToAncestor(table).TransformBounds(new Rect(verticalScrollBar.RenderSize));
                Rect horizontalBounds = horizontalScrollBar.TransformToAncestor(table).TransformBounds(new Rect(horizontalScrollBar.RenderSize));
                Assert.IsTrue(horizontalBounds.Right <= verticalBounds.Left);
                foreach (ScrollBar bar in new[] { verticalScrollBar, horizontalScrollBar })
                {
                    var track = (Track)bar.Template.FindName("PART_Track", bar);
                    var trackChrome = (Border)bar.Template.FindName("TrackChrome", bar);
                    Assert.AreSame(switchedThemeTrackBackground, trackChrome.Background);
                    track.Thumb.ApplyTemplate();
                    var thumbChrome = (Border)track.Thumb.Template.FindName("ThumbChrome", track.Thumb);
                    Assert.AreSame(table.FindResource("ScrollBar.ThumbBackgroundBrush"), thumbChrome.Background);
                    AssertListScrollBarGeometry(table, bar, trackChrome, thumbChrome, tableWidth, tableHeight);
                }
                AssertListScrollBarHitTesting(table, verticalScrollBar, horizontalScrollBar);

                // 無効時も背景の実効Opacityは1のまま、無効表示はつまみ側だけに掛かる。
                foreach (ScrollBar bar in new[] { verticalScrollBar, horizontalScrollBar })
                {
                    var trackChrome = (Border)bar.Template.FindName("TrackChrome", bar);
                    var track = (Track)bar.Template.FindName("PART_Track", bar);
                    var thumbChrome = (Border)track.Thumb.Template.FindName("ThumbChrome", track.Thumb);
                    double enabledThumbOpacity = GetEffectiveOpacity(thumbChrome, table);
                    bar.IsEnabled = false;
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreEqual(1d, GetEffectiveOpacity(trackChrome, table), 0.0001d, "無効時も一覧の背景は不透明のまま。");
                    Assert.IsTrue(GetEffectiveOpacity(thumbChrome, table) < enabledThumbOpacity, "無効表示はつまみ側へ限定する。");
                    Assert.AreNotSame(table.FindResource("ScrollBar.ThumbBackgroundBrush"), thumbChrome.Background);
                    bar.IsEnabled = true;
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                }

                // テーマを戻しても背景とつまみが追随し、選択・スクロール位置は表を作り直さず保たれる。
                table.SelectedIndex = 3;
                verticalScrollBar.Value = 2d;
                horizontalScrollBar.Value = 10d;
                // 本番の編集入口（F2）で編集を始め、入力途中のdraftがテーマ切替後も同じeditorに残ることを確認する。
                RaiseKey(table, Key.F2);
                TextBox editorBefore = GetInstalledEditor(table) ?? throw new AssertFailedException("編集controlへ到達しませんでした。");
                editorBefore.Text = "draft-before-theme-switch";
                CustomTableColumn contractColumn = table.Columns.Single();
                Assert.IsTrue(table.IsCurrentCell(3, contractColumn));
                int selectedBefore = table.SelectedIndex;
                int firstRowBefore = table.FirstVisibleRowIndex;
                double horizontalOffsetBefore = table.HorizontalOffset;
                Settings.Default.AppearanceTheme = previousTheme;
                application.Resources.Remove(trackBackgroundResourceKey);
                AppThemeService.ApplyTheme(previousTheme);
                FlushResourceUpdates(table);
                object restoredTrackBackground = table.FindResource(trackBackgroundResourceKey);
                Assert.AreNotSame(switchedThemeTrackBackground, restoredTrackBackground);
                foreach (ScrollBar bar in new[] { verticalScrollBar, horizontalScrollBar })
                {
                    var trackChrome = (Border)bar.Template.FindName("TrackChrome", bar);
                    var track = (Track)bar.Template.FindName("PART_Track", bar);
                    var thumbChrome = (Border)track.Thumb.Template.FindName("ThumbChrome", track.Thumb);
                    Assert.AreSame(restoredTrackBackground, trackChrome.Background);
                    Assert.AreSame(table.FindResource("ScrollBar.ThumbBackgroundBrush"), thumbChrome.Background);
                }
                Assert.AreEqual(selectedBefore, table.SelectedIndex);
                Assert.AreEqual(firstRowBefore, table.FirstVisibleRowIndex);
                Assert.AreEqual(horizontalOffsetBefore, table.HorizontalOffset, 0.0001d);
                Assert.IsTrue(table.IsCurrentCell(3, contractColumn));
                Assert.AreSame(editorBefore, GetInstalledEditor(table));
                Assert.AreEqual("draft-before-theme-switch", editorBefore.Text);
                RaiseKey(table, Key.Escape);
            }
            finally
            {
                if (resourceHostWindow is not null)
                {
                    resourceHostWindow.Content = null;
                    resourceHostWindow.Close();
                }
                application.Resources.Remove(trackBackgroundResourceKey);
                Settings.Default.AppearanceTheme = previousTheme;
                AppThemeService.ApplyTheme(previousTheme);
                if (activeThemeResources is not null && previousThemeTrackBackground is not null)
                {
                    activeThemeResources[trackBackgroundResourceKey] = previousThemeTrackBackground;
                }
                if (hadApplicationTrackBackground)
                {
                    application.Resources[trackBackgroundResourceKey] = previousApplicationTrackBackground;
                }
            }
        });
    }

    /// <summary>
    /// 実MainWindowの一覧2表で、背景とつまみが可視外枠の内縁から同じ補正で配置され、進捗行の有無で下端だけが変わることを確認します。
    /// ツリーのバーは同じ外枠補正を受けます。候補Popupへの非波及は、起動中の操作受付に依存しない別ケースで確認します。
    /// </summary>
    [TestMethod]
    public void MainWindowListScrollBars_AlignBothTablesToVisibleFrameAndKeepPopupOnSharedStyle()
    {
        WithSimpleTextBoxStyles(() => MainWindowPresentationTestHarness.RunConstructorOnly(
            TestSettingsFactory.CreateIsolatedSettings(), (scope, viewModel, window) =>
            {
                WithPresentedMainWindow(scope, window, () =>
                {
                    var frame = (Border)window.FindName("windowChromeFrame");
                    var mainTable = (CustomTableView)window.FindName("customTableView");
                    var summaryTable = (CustomTableView)window.FindName("customTablePlaylistSummary");
                    foreach (CustomTableView table in new[] { mainTable, summaryTable })
                    {
                        table.HeaderHeight = 0d;
                        table.RowHeight = 20d;
                        table.Columns = [CreateScrollBarContractColumn(6000d)];
                        table.ItemsSource = Enumerable.Range(0, 400).Select(_ => new object()).ToList();
                    }

                    // 進捗行なし: 右端と下端の双方が外枠の内縁へ接する。
                    Assert.IsFalse(viewModel.ProgressHub.HasRows);
                    AssertTableBarsAlignedToFrame(window, frame, mainTable, bottomCorrection: true);
                    viewModel.PlaylistWorkspace.SetPlaylistSummaryMode(true);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    AssertTableBarsAlignedToFrame(window, frame, summaryTable, bottomCorrection: true);

                    // 進捗行あり: 表の下に行が入り、横バーの下端補正は0に戻る。縦バーの右端補正は変わらない。
                    viewModel.ProgressHub.BeginStartupBackgroundInitializationPresentation(1, 1);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.IsTrue(viewModel.ProgressHub.HasRows);
                    AssertTableBarsAlignedToFrame(window, frame, summaryTable, bottomCorrection: false);
                    viewModel.PlaylistWorkspace.SetPlaylistSummaryMode(false);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    AssertTableBarsAlignedToFrame(window, frame, mainTable, bottomCorrection: false);
                    viewModel.ProgressHub.ResetStartupBackgroundInitializationPresentation();
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.IsFalse(viewModel.ProgressHub.HasRows);
                    AssertTableBarsAlignedToFrame(window, frame, mainTable, bottomCorrection: true);

                    // Style未指定のバーが表の資源範囲から暗黙解決されても、一覧用にも外枠補正にもならない（暗黙Styleの再導入を検出する）。
                    foreach (CustomTableView table in new[] { mainTable, summaryTable })
                    {
                        // Style未指定と、共通キーを参照するバー（表Resourcesに同名の別名があれば一覧用が漏れる）の双方を確認する。
                        var implicitBar = new ScrollBar { Orientation = Orientation.Vertical };
                        var keyedBar = new ScrollBar { Orientation = Orientation.Vertical };
                        keyedBar.SetResourceReference(FrameworkElement.StyleProperty, "App.Canonical.ScrollBarStyle");
                        table.Children.Add(implicitBar);
                        table.Children.Add(keyedBar);
                        try
                        {
                            window.UpdateLayout();
                            foreach (ScrollBar bar in new[] { implicitBar, keyedBar })
                            {
                                Assert.IsFalse(ScrollBarChrome.GetIsOpaqueBackground(bar), $"{table.Name}: 表以外のバーへ一覧用を波及させない。");
                                Assert.AreEqual(new Thickness(0d), bar.Padding, $"{table.Name}: 表以外のバーへ外枠補正を波及させない。");
                            }
                        }
                        finally
                        {
                            table.Children.Remove(implicitBar);
                            table.Children.Remove(keyedBar);
                        }
                    }

                    // ツリーのStyleを当てたprobeで、外枠補正の各分岐をPaddingと背景の実矩形で確認する。
                    var upperStyle = (Style)((TreeView)window.FindName("treeViewPlaylist")).Resources["App.Canonical.ScrollBarStyle"];
                    var lowerStyle = (Style)((TreeView)window.FindName("treeView")).Resources["App.Canonical.ScrollBarStyle"];
                    var treePane = (Grid)window.FindName("gridTreePane");

                    // 1. MainWindow内・Popup外: 縦は右1。横の下端は上側0、下側は進捗行なしで1。
                    AssertProbe(treePane, window, upperStyle, Orientation.Vertical, 1d, "上側ツリー縦");
                    AssertProbe(treePane, window, upperStyle, Orientation.Horizontal, 0d, "上側ツリー横");
                    AssertProbe(treePane, window, lowerStyle, Orientation.Vertical, 1d, "下側ツリー縦");
                    AssertProbe(treePane, window, lowerStyle, Orientation.Horizontal, 1d, "下側ツリー横・進捗行なし");

                    // 4. 進捗行あり: 下側ツリーの下端は0に戻り、縦の右端1は変わらない。
                    viewModel.ProgressHub.BeginStartupBackgroundInitializationPresentation(1, 1);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    AssertProbe(treePane, window, lowerStyle, Orientation.Horizontal, 0d, "下側ツリー横・進捗行あり");
                    AssertProbe(treePane, window, lowerStyle, Orientation.Vertical, 1d, "下側ツリー縦・進捗行あり");
                    viewModel.ProgressHub.ResetStartupBackgroundInitializationPresentation();
                    TestUiDispatcherHost.ProcessQueuedPresentation();

                    // 2. Popup内: MainWindowの論理木へ接続していても縦横とも補正0。
                    // Popup分岐がなければ、同じ位置・条件（右端、進捗行なしの下端）で補正1になる。
                    var popupHost = new StackPanel();
                    var popup = new Popup { PlacementTarget = treePane, Child = popupHost };
                    treePane.Children.Add(popup);
                    try
                    {
                        DependencyObject? logicalAncestor = popupHost;
                        while (logicalAncestor is not null and not MainWindow) { logicalAncestor = LogicalTreeHelper.GetParent(logicalAncestor); }
                        Assert.AreSame(window, logicalAncestor, "Popup内のprobeがMainWindowへ論理的に到達する。");
                        foreach (Orientation orientation in new[] { Orientation.Vertical, Orientation.Horizontal })
                        {
                            popupHost.Children.Clear();
                            AssertProbeIn(popupHost, lowerStyle, orientation, 0d, $"Popup内{orientation}");
                        }
                    }
                    finally
                    {
                        treePane.Children.Remove(popup);
                    }

                    // 3. MainWindow祖先なし: 別Windowの配置では縦横とも補正0（進捗行なし扱いにしない）。
                    var otherWindow = new Window { Resources = window.Resources };
                    var otherHost = new Grid();
                    otherWindow.Content = otherHost;
                    foreach (Orientation orientation in new[] { Orientation.Vertical, Orientation.Horizontal })
                    {
                        otherHost.Children.Clear();
                        AssertProbeIn(otherHost, lowerStyle, orientation, 0d, $"祖先なし{orientation}");
                    }
                });
            }));
    }

    /// <summary>
    /// 本番の編集入口が生成した候補Popup内のバーが、一覧用にも外枠補正にもならず、通常背景を表示せず枠内2 DIPにつまみを置くことを確認します。
    /// 実MainWindowのF2は操作受付と実モーダル警告に依存するため、同じ編集入口を持つ表単体で確認し、MainWindowの補正は別ケースが担います。
    /// </summary>
    [TestMethod]
    public void CustomTableEditSuggestionPopup_KeepsSharedScrollBarStyle()
    {
        WithSimpleTextBoxStyles(() => TestUiDispatcherHost.RunWindowTest(scope =>
        {
            ChartFile chart = ChartFileProjection.WithPackageState(
                ChartTestValues.Empty(), string.Empty, string.Empty, string.Empty,
                [.. Enumerable.Range(0, 40).Select(index => $@"C:\scrollbar-popup\candidate{index}")], []);
            var row = LibraryChartRow.FromPackageChartEntry(PackageChartEntry.FromChart(chart));
            var settings = new CustomTableColumnSettings();
            settings.InstallDst.Visibility = Visibility.Visible;
            CustomTableColumn[] columns = [.. CustomTableColumnFactory.CreateMainColumns(settings).Where(column => column.Id == "InstallDst")];
            var table = new CustomTableView { Width = 480, Height = 110, HeaderHeight = 20, RowHeight = 40, ColumnsSettings = settings, Columns = columns, ItemsSource = new[] { row } };
            var popup = (Popup)(typeof(CustomTableView).GetField("editSuggestionPopup", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(table)
                ?? throw new InvalidOperationException("候補Popupが見つかりません。"));
            scope.TrackPopup(popup);
            try
            {
                scope.ShowAndWaitForContentRendered(new Window { Width = 520, Height = 160, Content = table });
                var point = new Point(15d, 35d);
                Assert.IsTrue(table.HandleCellMouseDown(table.HitTestTable(point), MouseButton.Left, ModifierKeys.None, 1, point));
                Assert.IsTrue(table.HandleKeyDown(Key.F2, ModifierKeys.None));
                Assert.IsTrue(table.HandleKeyDown(Key.Down, ModifierKeys.None));
                Assert.IsTrue(popup.IsOpen);
                var popupFrame = (Border)popup.Child;
                FindVisualDescendants<ListBox>(popupFrame).Single().MaxHeight = 80d;
                popupFrame.Measure(new Size(400d, 400d));
                popupFrame.Arrange(new Rect(0d, 0d, 400d, popupFrame.DesiredSize.Height));
                popupFrame.UpdateLayout();
                ScrollBar bar = FindVisualDescendants<ScrollBar>(popupFrame).Single(candidate => candidate.Orientation == Orientation.Vertical && candidate.Visibility == Visibility.Visible);
                Assert.IsFalse(ScrollBarChrome.GetIsOpaqueBackground(bar), "候補Popupへ一覧用の背景を波及させない。");
                Assert.AreEqual(new Thickness(0d), bar.Padding);
                bar.ApplyTemplate();
                Assert.AreEqual(0d, ((Border)bar.Template.FindName("TrackChrome", bar)).Opacity, "通常背景は非表示。");
                var track = (Track)bar.Template.FindName("PART_Track", bar);
                track.Thumb.ApplyTemplate();
                var thumb = (Border)track.Thumb.Template.FindName("ThumbChrome", track.Thumb);
                Rect thumbBounds = thumb.TransformToAncestor(popupFrame).TransformBounds(new Rect(thumb.RenderSize));
                Assert.AreEqual(2d, popupFrame.ActualWidth - popupFrame.BorderThickness.Right - thumbBounds.Right, 0.25d, "枠内2 DIP。");
            }
            finally { table.HandleKeyDown(Key.Escape, ModifierKeys.None); table.ItemsSource = null; }
        }));
    }

    /// <summary>ツリー用StyleのバーをMainWindow内の配置先へ置き、外枠補正と背景の外端の位置を実矩形で確認します。</summary>
    private static void AssertProbe(Panel host, MainWindow window, Style style, Orientation orientation, double expectedCorrection, string description)
    {
        var probe = new ScrollBar { Orientation = orientation, Style = style, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
        if (orientation == Orientation.Vertical) { probe.Height = 100d; } else { probe.Width = 100d; }
        host.Children.Add(probe);
        try
        {
            window.UpdateLayout();
            AssertProbeGeometry(probe, expectedCorrection, description);
        }
        finally
        {
            host.Children.Remove(probe);
        }
    }

    /// <summary>MainWindow外（Popup・別Window）の配置先へ置いたバーを手動で配置し、外枠補正と背景の外端の位置を確認します。</summary>
    private static void AssertProbeIn(Panel host, Style style, Orientation orientation, double expectedCorrection, string description)
    {
        var probe = new ScrollBar { Orientation = orientation, Style = style };
        if (orientation == Orientation.Vertical) { probe.Height = 100d; } else { probe.Width = 100d; }
        host.Children.Add(probe);
        host.Measure(new Size(200d, 200d));
        host.Arrange(new Rect(0d, 0d, 200d, 200d));
        host.UpdateLayout();
        AssertProbeGeometry(probe, expectedCorrection, description);
    }

    private static void AssertProbeGeometry(ScrollBar probe, double expectedCorrection, string description)
    {
        Assert.IsFalse(ScrollBarChrome.GetIsOpaqueBackground(probe), $"{description}: ツリー用は既存用の背景種別。");
        bool isVertical = probe.Orientation == Orientation.Vertical;
        Assert.AreEqual(expectedCorrection, isVertical ? probe.Padding.Right : probe.Padding.Bottom, 0.0001d, $"{description}: Padding");
        probe.ApplyTemplate();
        var background = (Border)probe.Template.FindName("TrackChrome", probe);
        Rect bounds = background.TransformToAncestor(probe).TransformBounds(new Rect(background.RenderSize));
        Assert.AreEqual(isVertical ? probe.ActualWidth - expectedCorrection : probe.ActualHeight - expectedCorrection,
            isVertical ? bounds.Right : bounds.Bottom, 0.25d, $"{description}: 背景の外端");
    }

    /// <summary>
    /// 表の縦横バーの背景とつまみを実矩形で測り、背景の外端が可視外枠の内縁と一致すること、下端だけが進捗行の有無で補正されることを確認します。
    /// 右端は常に外枠へ接し、背景7 DIP・つまみ3 DIP・両側各2 DIPが成立します。
    /// </summary>
    private static void AssertTableBarsAlignedToFrame(MainWindow window, Border frame, CustomTableView table, bool bottomCorrection)
    {
        window.UpdateLayout();
        TestUiDispatcherHost.ProcessQueuedPresentation();
        ScrollBar vertical = table.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == Orientation.Vertical);
        ScrollBar horizontal = table.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == Orientation.Horizontal);
        Assert.AreEqual(Visibility.Visible, vertical.Visibility);
        Assert.AreEqual(Visibility.Visible, horizontal.Visibility);
        Assert.IsTrue(ScrollBarChrome.GetIsOpaqueBackground(vertical) && ScrollBarChrome.GetIsOpaqueBackground(horizontal),
            $"{table.Name}の縦横とも一覧用。");
        // 外枠は表の祖先ではない兄弟なので、Window座標で可視枠の内縁を求める。
        Rect frameBounds = frame.TransformToAncestor(window).TransformBounds(new Rect(frame.RenderSize));
        double frameRight = frameBounds.Right - frame.BorderThickness.Right;
        double frameBottom = frameBounds.Bottom - frame.BorderThickness.Bottom;
        foreach (ScrollBar bar in new[] { vertical, horizontal })
        {
            bar.ApplyTemplate();
            var background = (Border)bar.Template.FindName("TrackChrome", bar);
            var track = (Track)bar.Template.FindName("PART_Track", bar);
            track.Thumb.ApplyTemplate();
            var thumb = (Border)track.Thumb.Template.FindName("ThumbChrome", track.Thumb);
            Rect backgroundBounds = background.TransformToAncestor(window).TransformBounds(new Rect(background.RenderSize));
            Rect thumbBounds = thumb.TransformToAncestor(window).TransformBounds(new Rect(thumb.RenderSize));
            Assert.AreEqual(1d, GetEffectiveOpacity(background, window), 0.0001d, $"{table.Name}: 背景は不透明。");
            if (bar.Orientation == Orientation.Vertical)
            {
                Assert.AreEqual(frameRight, backgroundBounds.Right, 0.25d, $"{table.Name}: 縦バーの背景は可視枠の右内縁。");
                Assert.AreEqual(7d, backgroundBounds.Width, 0.25d);
                Assert.AreEqual(3d, thumbBounds.Width, 0.25d);
                Assert.AreEqual(2d, backgroundBounds.Right - thumbBounds.Right, 0.25d);
                Assert.AreEqual(2d, thumbBounds.Left - backgroundBounds.Left, 0.25d);
            }
            else
            {
                if (bottomCorrection)
                {
                    Assert.AreEqual(frameBottom, backgroundBounds.Bottom, 0.25d, $"{table.Name}: 進捗行なしの横バー背景は可視枠の下内縁。");
                }
                else
                {
                    Assert.IsTrue(backgroundBounds.Bottom < frameBottom - 0.5d, $"{table.Name}: 進捗行ありは下枠に接しない。");
                    Rect barBounds = bar.TransformToAncestor(window).TransformBounds(new Rect(bar.RenderSize));
                    Assert.AreEqual(barBounds.Bottom, backgroundBounds.Bottom, 0.25d, $"{table.Name}: 補正0。");
                }
                Assert.AreEqual(7d, backgroundBounds.Height, 0.25d);
                Assert.AreEqual(3d, thumbBounds.Height, 0.25d);
                Assert.AreEqual(2d, backgroundBounds.Bottom - thumbBounds.Bottom, 0.25d);
                Assert.AreEqual(2d, thumbBounds.Top - backgroundBounds.Top, 0.25d);
            }
        }
    }

    /// <summary>compiled検索TextBoxの編集を通常譜面のfilterへ配送し、editor内の値だけを更新して一覧検索へ届かない接続漏れを検出します。</summary>
    [TestMethod]
    public void KeywordEditorTextUpdatesComposedChartFilter()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                TestSettingsFactory.CreateIsolatedSettings(),
                (viewModel, window) =>
                {
                    KeywordSearchEditor searchEditor = GetNamedElement<KeywordSearchEditor>(window, "KeywordSearchEditor");
                    TextBox keyword = searchEditor.InputTextBoxControl;
                    keyword.Text = "wave6e-filter";
                    BindingOperations.GetBindingExpression(searchEditor, KeywordSearchEditor.TextProperty)?.UpdateSource();
                    Assert.AreEqual("wave6e-filter", viewModel.ChartFilters.KeywordFilter);
                });
        });
    }

    /// <summary>通常譜面とサマリーの警告表示を切り替えてもヘルプ・clear操作の位置を維持し、警告の出現で操作位置が移動しません。</summary>
    [TestMethod]
    public void SearchToolbars_WarningVisibilityKeepsNeighborPositions()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                TestSettingsFactory.CreateIsolatedSettings(),
                (viewModel, window) =>
                {
                    MaterializeMainWindow(window);

                    SearchChrome normal = GetSearchChrome(window, "KeywordSearchEditor");
                    Point normalHelpBeforeWarning = GetRelativeOrigin(normal.HelpSlot, normal.Outer);
                    Point normalClearBeforeWarning = GetRelativeOrigin(normal.ClearSlot, normal.Outer);
                    Assert.AreEqual(Visibility.Collapsed, normal.WarningContent.Visibility);
                    normal.WarningContent.SetCurrentValue(
                        UIElement.VisibilityProperty,
                        Visibility.Visible);
                    MaterializeMainWindow(window);
                    Assert.AreEqual(Visibility.Visible, normal.WarningContent.Visibility);
                    AssertSlotChildCentered(normal.WarningSlot, normal.WarningContent, normal.Outer);
                    AssertSamePoint(normalHelpBeforeWarning, GetRelativeOrigin(normal.HelpSlot, normal.Outer));
                    AssertSamePoint(normalClearBeforeWarning, GetRelativeOrigin(normal.ClearSlot, normal.Outer));
                    normal.WarningContent.SetCurrentValue(
                        UIElement.VisibilityProperty,
                        Visibility.Collapsed);
                    MaterializeMainWindow(window);
                    Assert.AreEqual(Visibility.Collapsed, normal.WarningContent.Visibility);
                    AssertSamePoint(normalHelpBeforeWarning, GetRelativeOrigin(normal.HelpSlot, normal.Outer));
                    AssertSamePoint(normalClearBeforeWarning, GetRelativeOrigin(normal.ClearSlot, normal.Outer));

                    viewModel.PlaylistWorkspace.SetPlaylistSummaryMode(true);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    MaterializeMainWindow(window);

                    SearchChrome summary = GetSearchChrome(window, "PlaylistSummaryKeywordSearchEditor");
                    Assert.AreEqual(Visibility.Collapsed, summary.WarningContent.Visibility);
                    Point summaryHelpWithoutWarning = GetRelativeOrigin(summary.HelpSlot, summary.Outer);
                    Point summaryClearWithoutWarning = GetRelativeOrigin(summary.ClearSlot, summary.Outer);
                    summary.WarningContent.SetCurrentValue(
                        UIElement.VisibilityProperty,
                        Visibility.Visible);
                    MaterializeMainWindow(window);
                    Assert.AreEqual(Visibility.Visible, summary.WarningContent.Visibility);
                    AssertSlotChildCentered(summary.WarningSlot, summary.WarningContent, summary.Outer);
                    AssertSamePoint(summaryHelpWithoutWarning, GetRelativeOrigin(summary.HelpSlot, summary.Outer));
                    AssertSamePoint(summaryClearWithoutWarning, GetRelativeOrigin(summary.ClearSlot, summary.Outer));
                    summary.WarningContent.SetCurrentValue(
                        UIElement.VisibilityProperty,
                        Visibility.Collapsed);
                    MaterializeMainWindow(window);
                    Assert.AreEqual(Visibility.Collapsed, summary.WarningContent.Visibility);
                    AssertSamePoint(summaryHelpWithoutWarning, GetRelativeOrigin(summary.HelpSlot, summary.Outer));
                    AssertSamePoint(summaryClearWithoutWarning, GetRelativeOrigin(summary.ClearSlot, summary.Outer));
                });
        });
    }

    /// <summary>compiledメニューの7種と5解除後の24/48保持、最後の解除拒否を確認し、通常とサマリーの条件を独立させます。</summary>
    [TestMethod]
    public void SearchVariants_FilterMenusChangeOnlyCorrespondingOwner()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                TestSettingsFactory.CreateIsolatedSettings(),
                (viewModel, window) =>
                {
                    SearchChrome normal = GetSearchChrome(window, "KeywordSearchEditor");
                    SearchChrome summary = GetSearchChrome(window, "PlaylistSummaryKeywordSearchEditor");
                    AssertFilterAffordanceMaterialized(normal);
                    AssertFilterAffordanceMaterialized(summary);
                    MaterializeMainWindow(window);
                    AssertFilterAffordanceRendered(normal);
                    PlaylistOwnedFilter ownedFilterBeforeInteraction = viewModel.PlaylistWorkspace.PlaylistSummaryOwnedFilter;
                    MenuItem[] modeItems = normal.FilterButton.DropDownContextMenu!.Items.OfType<MenuItem>().ToArray();
                    CollectionAssert.AreEquivalent(new[] { "5KEYS", "7KEYS", "9KEYS", "10KEYS", "14KEYS", "24KEYS", "48KEYS" },
                        modeItems.Select(item => ((string)item.Header).Trim()).ToArray());
                    foreach (MenuItem item in modeItems)
                    {
                        BindingOperations.GetBindingExpression(item, MenuItem.IsCheckedProperty)?.UpdateTarget();
                        Assert.IsTrue(item.IsChecked);
                    }
                    Dictionary<string, MenuItem> modeItemsByName = modeItems.ToDictionary(item => ((string)item.Header).Trim());
                    SetFilterMenuItemChecked(modeItemsByName["5KEYS"], false);
                    Assert.AreEqual(ChartModeFilter._7KEYS | ChartModeFilter._9KEYS | ChartModeFilter._10KEYS
                        | ChartModeFilter._14KEYS | ChartModeFilter._24KEYS | ChartModeFilter._48KEYS, viewModel.ChartFilters.ModeFilter);
                    Assert.IsTrue(modeItemsByName["24KEYS"].IsChecked);
                    Assert.IsTrue(modeItemsByName["48KEYS"].IsChecked);
                    foreach (string modeName in new[] { "7KEYS", "9KEYS", "10KEYS", "14KEYS", "24KEYS" })
                    {
                        SetFilterMenuItemChecked(modeItemsByName[modeName], false);
                    }
                    Assert.AreEqual(ChartModeFilter._48KEYS, viewModel.ChartFilters.ModeFilter);
                    SetFilterMenuItemChecked(modeItemsByName["48KEYS"], false);
                    Assert.AreEqual(ChartModeFilter._48KEYS, viewModel.ChartFilters.ModeFilter);
                    Assert.IsTrue(modeItemsByName["48KEYS"].IsChecked);
                    Assert.AreEqual(ownedFilterBeforeInteraction, viewModel.PlaylistWorkspace.PlaylistSummaryOwnedFilter);
                    ChartModeFilter modeAfterNormalInteraction = viewModel.ChartFilters.ModeFilter;

                    viewModel.PlaylistWorkspace.SetPlaylistSummaryMode(true);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    MaterializeMainWindow(window);
                    AssertFilterAffordanceRendered(summary);
                    MenuItem summaryFilterItem = GetFilterMenuItem(summary.FilterButton.DropDownContextMenu!, false);
                    SetFilterMenuItemChecked(summaryFilterItem, true);
                    Assert.AreEqual(modeAfterNormalInteraction, viewModel.ChartFilters.ModeFilter);
                    Assert.AreNotEqual(ownedFilterBeforeInteraction, viewModel.PlaylistWorkspace.PlaylistSummaryOwnedFilter);
                });
        });
    }


    /// <summary>clearアイコンのmouse操作で検索入力を空にして補助popupを更新し、サマリー側へ通常譜面の保存クエリを混入させません。</summary>
    [TestMethod]
    public void SearchEditor_ClearMouseRouteRefreshesOnlyItsOwnEmptyAssistanceImmediately()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                TestSettingsFactory.CreateIsolatedSettings(),
                (windowTest, viewModel, window) =>
                {
                    SearchChrome normal = GetSearchChrome(window, "KeywordSearchEditor");
                    SearchChrome summary = GetSearchChrome(window, "PlaylistSummaryKeywordSearchEditor");
                    KeywordSearchAssistanceOwner normalOwner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;

                    Assert.IsTrue(normalOwner.TryAddFavorite("title:favorite").Succeeded);
                    Assert.IsTrue(normalOwner.SavedQueryOwner.TryCommitHistory("artist:history").Succeeded);

                    WithPresentedMainWindow(windowTest, window, () =>
                    {
                        PrepareSearchEditor(normal, normalOwner, "title:normal");
                        TestUiDispatcherHost.ProcessQueuedPresentation();

                        RaiseMouseLeftButtonDown(normal.ClearIcon);
                        Assert.AreEqual(string.Empty, normal.TextBox.Text);
                        Assert.IsTrue(normal.SuggestionPopup.IsOpen);
                        CollectionAssert.AreEqual(
                            new[]
                            {
                                KeywordSearchPresentationSectionKind.Favorites,
                                KeywordSearchPresentationSectionKind.History,
                                KeywordSearchPresentationSectionKind.Fields
                            },
                            normal.Editor.Presentation.Sections.Select(section => section.Kind).ToArray());

                        viewModel.PlaylistWorkspace.SetPlaylistSummaryMode(true);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        KeywordSearchAssistanceOwner summaryOwner = viewModel.PlaylistWorkspace.PlaylistSummaryKeywordSearchAssistanceOwner;
                        PrepareSearchEditor(summary, summaryOwner, "name:summary");
                        TestUiDispatcherHost.ProcessQueuedPresentation();

                        RaiseMouseLeftButtonDown(summary.ClearIcon);
                        Assert.AreEqual(string.Empty, summary.TextBox.Text);
                        Assert.IsTrue(summary.SuggestionPopup.IsOpen);
                        Assert.IsTrue(
                            summary.Editor.Presentation.Sections.Any(
                                section => section.Kind == KeywordSearchPresentationSectionKind.Fields));
                        Assert.IsFalse(
                            summary.Editor.Presentation.VisibleItems.Any(
                                item => string.Equals(item.Query, "title:favorite", StringComparison.Ordinal)
                                    || string.Equals(item.Query, "artist:history", StringComparison.Ordinal)));
                    });
                });
        });
    }

    /// <summary>入力変更で補助popupを即時更新してprefix候補へ絞り、空へ戻すと保存したお気に入り・履歴・フィールドのsectionを復元します。</summary>
    [TestMethod]
    public void SearchEditor_RefreshesImmediatelyAndRestoresEmptySections()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                TestSettingsFactory.CreateIsolatedSettings(),
                (windowTest, viewModel, window) =>
                {
                    SearchChrome chrome = GetSearchChrome(window, "KeywordSearchEditor");
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    Assert.IsTrue(owner.TryAddFavorite("title:alpha").Succeeded);
                    Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory("artist:beta").Succeeded);

                    WithPresentedMainWindow(windowTest, window, () =>
                    {
                        PrepareSearchEditor(chrome, owner, string.Empty);
                        TestUiDispatcherHost.ProcessQueuedPresentation();

                        Assert.IsTrue(
                            chrome.SuggestionPopup.IsOpen,
                            $"popup={chrome.SuggestionPopup.IsOpen}, ownerOpen={owner.Presentation.IsOpen}, editorOpen={chrome.Editor.Presentation.IsOpen}, sections={owner.Presentation.Sections.Count}");
                        CollectionAssert.AreEqual(
                            new[]
                            {
                                KeywordSearchPresentationSectionKind.Favorites,
                                KeywordSearchPresentationSectionKind.History,
                                KeywordSearchPresentationSectionKind.Fields
                            },
                            chrome.Editor.Presentation.Sections.Select(section => section.Kind).ToArray());
                        Assert.AreEqual("title:alpha", chrome.Editor.Presentation.Sections[0].Items[0].Query);
                        Assert.AreEqual("artist:beta", chrome.Editor.Presentation.Sections[1].Items[0].Query);

                        chrome.TextBox.Text = "tit";
                        chrome.TextBox.CaretIndex = chrome.TextBox.Text.Length;
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.IsTrue(chrome.SuggestionPopup.IsOpen);
                        Assert.AreEqual(1, chrome.Editor.Presentation.Sections.Count);
                        Assert.AreEqual(
                            KeywordSearchPresentationSectionKind.Fields,
                            chrome.Editor.Presentation.Sections[0].Kind);
                        Assert.IsTrue(
                            chrome.Editor.Presentation.Sections[0].Items.All(
                                item => item.Kind == KeywordSearchPresentationItemKind.Field));

                        chrome.TextBox.Text = string.Empty;
                        chrome.TextBox.CaretIndex = 0;
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.IsTrue(chrome.SuggestionPopup.IsOpen);
                        Assert.AreEqual(3, chrome.Editor.Presentation.Sections.Count);

                    });
                });
        });
    }

    /// <summary>候補を上下選択してEnterで入力へ適用し、IME処理キーでは入力を変えず、Escapeで閉じたpopupをDownで再開します。</summary>
    [TestMethod]
    public void SearchEditor_NavigationAndImeKeepInputLifecycleOwnedByEditor()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                TestSettingsFactory.CreateIsolatedSettings(),
                (windowTest, viewModel, window) =>
                {
                    SearchChrome chrome = GetSearchChrome(window, "KeywordSearchEditor");
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    Assert.IsTrue(owner.TryAddFavorite("title:alpha").Succeeded);
                    Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory("artist:beta").Succeeded);

                    WithPresentedMainWindow(windowTest, window, () =>
                    {
                        PrepareSearchEditor(chrome, owner, string.Empty);
                        TestUiDispatcherHost.ProcessQueuedPresentation();

                        RaiseSearchKey(chrome.Editor, Key.Down);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.IsTrue(chrome.SuggestionPopup.IsOpen);
                        RaiseSearchKey(chrome.Editor, Key.Down);
                        TestUiDispatcherHost.ProcessQueuedPresentation();

                        string beforeIme = chrome.TextBox.Text;
                        RaiseSearchKey(chrome.Editor, Key.ImeProcessed);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.AreEqual(beforeIme, chrome.TextBox.Text);
                        Assert.IsTrue(chrome.SuggestionPopup.IsOpen);

                        RaiseSearchKey(chrome.Editor, Key.Enter);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.AreEqual("artist:beta", chrome.TextBox.Text);
                        Assert.AreEqual(chrome.TextBox.Text.Length, chrome.TextBox.CaretIndex);

                        chrome.TextBox.Text = string.Empty;
                        chrome.TextBox.CaretIndex = 0;
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.IsTrue(chrome.SuggestionPopup.IsOpen);
                        RaiseSearchKey(chrome.Editor, Key.Escape);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.IsFalse(chrome.SuggestionPopup.IsOpen);
                        Assert.IsFalse(owner.Presentation.IsOpen);

                        RaiseSearchKey(chrome.Editor, Key.Down);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.IsTrue(chrome.SuggestionPopup.IsOpen);
                    });
                });
        });
    }

    /// <summary>保存候補の削除とお気に入り追加を実buttonから実行し、検索入力と元履歴を変えず、操作を識別できる名称を表示します。</summary>
    [TestMethod]
    public void SearchEditor_SavedRowActionsKeepQueryUnchanged()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                TestSettingsFactory.CreateIsolatedSettings(),
                (windowTest, viewModel, window) =>
                {
                    SearchChrome chrome = GetSearchChrome(window, "KeywordSearchEditor");
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    Assert.IsTrue(owner.TryAddFavorite("title:favorite").Succeeded);
                    Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory("artist:history").Succeeded);

                    WithPresentedMainWindow(windowTest, window, () =>
                    {
                        PrepareSearchEditor(chrome, owner, string.Empty);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        // 操作ボタンの名称は各行のLoadedで付くため、Backgroundまで消化する。
                        TestUiDispatcherHost.ProcessQueuedBackground();

                        Border favoriteRow = FindSavedRow(chrome.Editor, "title:favorite");
                        Button removeFavorite = FindActionButton(
                            favoriteRow,
                            Resources.Keyword_search_remove_favorite_tooltip);
                        Assert.IsFalse(removeFavorite.IsDefault);
                        Assert.IsNotNull(removeFavorite.ToolTip);
                        Assert.AreEqual(
                            Resources.Keyword_search_remove_favorite_tooltip,
                            AutomationProperties.GetName(removeFavorite));
                        removeFavorite.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, removeFavorite));
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        // クリックで候補行が再実体化され、操作ボタンの名称が各行のLoadedで付くため、Backgroundまで消化する。
                        TestUiDispatcherHost.ProcessQueuedBackground();
                        Assert.AreEqual(string.Empty, chrome.TextBox.Text);
                        CollectionAssert.DoesNotContain(owner.SavedQueryOwner.Favorites.ToArray(), "title:favorite");

                        Border historyRow = FindSavedRow(chrome.Editor, "artist:history");
                        Button addFavorite = FindActionButton(
                            historyRow,
                            Resources.Keyword_search_add_favorite_tooltip);
                        addFavorite.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, addFavorite));
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.AreEqual(string.Empty, chrome.TextBox.Text);
                        CollectionAssert.Contains(owner.SavedQueryOwner.Favorites.ToArray(), "artist:history");
                        CollectionAssert.Contains(owner.SavedQueryOwner.History.ToArray(), "artist:history");
                    });
                });
        });
    }

    /// <summary>保存候補5/6行・補完10/11行の完全表示境界、枠内2 DIP・ボタン保護・独立offsetを実Popupで確認し、サマリーでは専用output候補だけを提供して未対応値候補を出しません。</summary>
    [TestMethod]
    public void SearchEditor_UsesIndependentSectionViewportsAndSummaryFieldCatalog()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                TestSettingsFactory.CreateIsolatedSettings(),
                (windowTest, viewModel, window) =>
                {
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    for (int index = 0; index < 6; index++)
                    {
                        Assert.IsTrue(owner.TryAddFavorite($"title:f{index}").Succeeded);
                        Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory($"artist:h{index}").Succeeded);
                    }

                    SearchChrome normal = GetSearchChrome(window, "KeywordSearchEditor");
                    WithPresentedMainWindow(windowTest, window, () =>
                    {
                        PrepareSearchEditor(normal, owner, string.Empty);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        // 操作ボタンの名称と区画のスクロール設定は各行・各区画のLoadedで付くため、Backgroundまで消化する。
                        TestUiDispatcherHost.ProcessQueuedBackground();

                        ScrollViewer[] sectionViewports = FindVisualDescendants<ScrollViewer>(
                                normal.Editor.AssistancePopupControl.Child!)
                            .ToArray();
                        Assert.AreEqual(3, sectionViewports.Length);
                        Assert.IsTrue(sectionViewports.All(viewport => viewport.MaxHeight > 0d && double.IsFinite(viewport.MaxHeight)));
                        Assert.IsTrue(sectionViewports.All(viewport => viewport.VerticalScrollBarVisibility == ScrollBarVisibility.Auto));
                        Button[] actionButtons = FindVisualDescendants<Button>(
                                normal.Editor.AssistancePopupControl.Child!)
                            .Where(button => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)))
                            .Where(button => button.Content is string)
                            .Where(button => button.Visibility == Visibility.Visible && button.IsVisible)
                            .ToArray();
                        Assert.IsTrue(actionButtons.Length >= 6);
                        Assert.IsTrue(actionButtons.All(button => button.ActualWidth > 0d && button.ActualHeight > 0d));
                        Assert.IsTrue(actionButtons.All(button => button.IsHitTestVisible));

                        ListBox favorites = GetSectionRows(normal.Editor, KeywordSearchPresentationSectionKind.Favorites);
                        ListBox history = GetSectionRows(normal.Editor, KeywordSearchPresentationSectionKind.History);
                        ListBox fields = GetSectionRows(normal.Editor, KeywordSearchPresentationSectionKind.Fields);
                        AssertCandidateRowLimit(favorites, 5);
                        AssertCandidateRowLimit(fields, 10);
                        ScrollViewer favoriteViewer = FindVisualDescendants<ScrollViewer>(favorites).Single();
                        ScrollViewer historyViewer = FindVisualDescendants<ScrollViewer>(history).Single();
                        Assert.AreEqual(6, favorites.Items.Count);
                        Assert.IsTrue(favoriteViewer.ScrollableHeight > 0d);
                        double historyOffset = historyViewer.VerticalOffset;
                        var scrolled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        ScrollChangedEventHandler scrolledHandler = (_, _) =>
                        {
                            if (favoriteViewer.VerticalOffset > 0d) { scrolled.TrySetResult(); }
                        };
                        favoriteViewer.ScrollChanged += scrolledHandler;
                        try
                        {
                            favoriteViewer.ScrollToEnd();
                            TestUiDispatcherHost.AwaitPresentationOnDispatcher(scrolled.Task, "search-favorites.scrolled");
                        }
                        finally { favoriteViewer.ScrollChanged -= scrolledHandler; }
                        Assert.IsTrue(favoriteViewer.VerticalOffset > 0d);
                        Assert.AreEqual(historyOffset, historyViewer.VerticalOffset, "各候補sectionのoffsetを共有しない。");
                        var popupBorder = (Border)normal.Editor.AssistancePopupControl.Child;
                        foreach (ListBox rows in new[] { favorites, history, fields })
                        {
                            ScrollViewer viewport = FindVisualDescendants<ScrollViewer>(rows).Single();
                            var bar = (ScrollBar)viewport.Template.FindName("PART_VerticalScrollBar", viewport);
                            Assert.AreEqual(Visibility.Visible, bar.Visibility);
                            bar.ApplyTemplate();
                            var track = (Track)bar.Template.FindName("PART_Track", bar);
                            track.Thumb.ApplyTemplate();
                            var chrome = (Border)track.Thumb.Template.FindName("ThumbChrome", track.Thumb);
                            Rect thumb = chrome.TransformToAncestor(popupBorder).TransformBounds(new Rect(chrome.RenderSize));
                            Assert.AreEqual(2d, popupBorder.ActualWidth - popupBorder.BorderThickness.Right - thumb.Right, 0.25d);
                            Rect barBounds = bar.TransformToAncestor(rows).TransformBounds(new Rect(bar.RenderSize));
                            foreach (Button action in FindVisualDescendants<Button>(rows).Where(button => button.IsVisible))
                            {
                                Rect bounds = action.TransformToAncestor(rows).TransformBounds(new Rect(action.RenderSize));
                                Assert.IsTrue(bounds.Right <= barBounds.Left + 0.25d, "候補操作ボタンはバー入力域の手前に配置する。");
                            }
                        }

                        viewModel.PlaylistWorkspace.SetPlaylistSummaryMode(true);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        SearchChrome summary = GetSearchChrome(window, "PlaylistSummaryKeywordSearchEditor");
                        KeywordSearchAssistanceOwner summaryOwner =
                            viewModel.PlaylistWorkspace.PlaylistSummaryKeywordSearchAssistanceOwner;
                        PrepareSearchEditor(summary, summaryOwner, string.Empty);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.AreEqual(
                            Resources.Keyword_search_summary_editor_name,
                            AutomationProperties.GetName(summary.TextBox));
                        Assert.AreEqual(1, summary.Editor.Presentation.Sections.Count);
                        Assert.AreEqual(
                            KeywordSearchPresentationSectionKind.Fields,
                            summary.Editor.Presentation.Sections.Single().Kind);
                        Assert.IsTrue(
                            summary.Editor.Presentation.VisibleItems.Any(
                                item => string.Equals(item.DisplayText, "output:", StringComparison.Ordinal)));
                        Assert.IsFalse(
                            summary.Editor.Presentation.VisibleItems.Any(
                                item => item.Kind == KeywordSearchPresentationItemKind.Value));

                        summary.TextBox.Text = "output:x";
                        summary.TextBox.CaretIndex = summary.TextBox.Text.Length;
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.IsFalse(summary.Editor.Presentation.IsOpen);
                        Assert.IsFalse(summary.Editor.Presentation.Sections.Any(
                            section => section.Kind == KeywordSearchPresentationSectionKind.Values));
                    });
                });
        });
    }

    /// <summary>概要の見出しまたは件数へ指定した経路でBindingされた、実TextBlockを取得します。</summary>
    private static TextBlock FindSummaryBindingTextBlock(DependencyObject root, string bindingPath)
        => FindVisualDescendants<TextBlock>(root).Single(block =>
            System.Windows.Data.BindingOperations.GetBinding(block, TextBlock.TextProperty)?.Path?.Path == bindingPath);

    /// <summary>通常とサマリーの実警告Tooltip・開いたhelpを言語切替で更新し、入力・caret・候補選択を保持します。実Close完了後は保持した両ownerへ言語通知を送りません。</summary>
    /// <remarks>外側ownerの通知から実BindingとPopup本文への接続、およびMainWindowの終了時解除を確認します。文脈・警告の組合せと再接続の条件網羅は各ownerテストへ分担します。</remarks>
    [TestMethod]
    public void SearchLanguage_UpdatesOuterBindingsAndDetachesBothOwnersAfterClose()
    {
        WithSimpleTextBoxStyles(() =>
        {
            CultureInfo? previousCulture = Resources.Culture;
            try
            {
                MainWindowPresentationTestHarness.RunConstructorOnly(
                    TestSettingsFactory.CreateIsolatedSettings(),
                    (windowTest, viewModel, window) =>
                    {
                        ChartListFilterViewModel filters = viewModel.ChartFilters;
                        PlaylistWorkspaceViewModel workspace = viewModel.PlaylistWorkspace;
                        WithPresentedMainWindow(windowTest, window, () =>
                        {
                            foreach (bool summary in new[] { false, true })
                            {
                                ResourceService.Current.ChangeCulture("ja-JP");
                                workspace.SetPlaylistSummaryMode(summary);
                                if (summary)
                                {
                                    // 件数を非空にするため、自動件数の概要を1行で採用する。
                                    Assert.IsTrue(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
                                    {
                                        Rows = new System.Collections.ObjectModel.ObservableCollection<PlaylistSummaryRow>([new PlaylistSummaryRow { PlaylistId = 1, Name = "alpha", TotalCharts = 3 }]),
                                        PresentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration()
                                    }));
                                }
                                SearchChrome chrome = GetSearchChrome(window, summary ? "PlaylistSummaryKeywordSearchEditor" : "KeywordSearchEditor");
                                KeywordSearchAssistanceOwner assistance = summary
                                    ? workspace.PlaylistSummaryKeywordSearchAssistanceOwner
                                    : filters.KeywordSearchAssistanceOwner;
                                string raw = summary ? "  unknown:alpha na" : "  unknown:alpha ti";
                                if (summary) { workspace.PlaylistSummaryKeywordFilter = raw; }
                                else { filters.KeywordFilter = raw; }
                                TestUiDispatcherHost.ProcessQueuedPresentation();
                                PrepareSearchEditor(chrome, assistance, raw);
                                chrome.TextBox.CaretIndex = raw.Length - 1;
                                RaiseSearchKey(chrome.Editor, Key.Down);
                                if (summary) { workspace.IsPlaylistSummaryKeywordSearchHelpOpen = true; }
                                else { filters.IsKeywordSearchHelpOpen = true; }
                                TestUiDispatcherHost.ProcessQueuedPresentation();
                                MaterializeMainWindow(window);
                                ListBox fields = GetSectionRows(chrome.Editor, KeywordSearchPresentationSectionKind.Fields);
                                var selected = (KeywordSearchPresentationItem)fields.SelectedItem;
                                Assert.IsNotNull(selected);
                                Assert.IsTrue(chrome.HelpPopup.IsOpen);
                                TextBlock help = FindVisualDescendants<TextBlock>(chrome.HelpPopup.Child).Single();
                                int caret = chrome.TextBox.CaretIndex;
                                long revision = assistance.Presentation.CatalogRevision;
                                int searches = 0, refreshes = 0;
                                EventHandler searchChanged = (_, _) => searches++;
                                EventHandler<PlaylistPresentationRefreshRequestedEventArgs> refreshRequested = (_, _) => refreshes++;
                                filters.KeywordFilterChanged += searchChanged;
                                workspace.PlaylistPresentationRefreshRequested += refreshRequested;
                                try
                                {
                                    ResourceService.Current.ChangeCulture("en-US");
                                    TestUiDispatcherHost.ProcessQueuedPresentation();
                                    Assert.AreEqual(string.Format(Resources.Keyword_search_warning_unknown_field, "unknown"), chrome.WarningContent.ToolTip);
                                    string fieldHelp = summary
                                        ? Resources.Keyword_search_help_fields_playlist_summary
                                        : Resources.Keyword_search_help_fields_bmsfile;
                                    Assert.AreEqual(string.Format(Resources.Keyword_search_help_template, fieldHelp), help.Text);
                                    Assert.IsTrue(chrome.HelpPopup.IsOpen);
                                    if (summary)
                                    {
                                        // 概要の見出しと件数は、実TextBlockのBindingが現在語の値へ追従する。
                                        TextBlock header = FindSummaryBindingTextBlock(window, "PlaylistWorkspace.GridHeaderText");
                                        TextBlock counts = FindSummaryBindingTextBlock(window, "PlaylistWorkspace.PlaylistSummaryText");
                                        Assert.AreEqual(Resources.Playlist_summary_header, workspace.GridHeaderText);
                                        Assert.AreEqual(Resources.Playlist_summary_header, header.Text);
                                        string expectedCounts = string.Format(Resources.Playlist_summary_format, workspace.PlaylistSummaryView.Sum(row => row.TotalCharts), workspace.PlaylistSummaryView.Count);
                                        Assert.AreNotEqual(string.Empty, expectedCounts);
                                        Assert.AreEqual(expectedCounts, workspace.PlaylistSummaryText);
                                        Assert.AreEqual(expectedCounts, counts.Text);
                                    }
                                    Assert.AreEqual(raw, chrome.TextBox.Text);
                                    Assert.AreEqual(caret, chrome.TextBox.CaretIndex);
                                    Assert.AreSame(fields, GetSectionRows(chrome.Editor, KeywordSearchPresentationSectionKind.Fields));
                                    Assert.AreEqual(selected.InsertionText, ((KeywordSearchPresentationItem)fields.SelectedItem).InsertionText);
                                    Assert.AreEqual(revision, assistance.Presentation.CatalogRevision);
                                    Assert.AreEqual(0, searches);
                                    Assert.AreEqual(0, refreshes);
                                }
                                finally
                                {
                                    filters.KeywordFilterChanged -= searchChanged;
                                    workspace.PlaylistPresentationRefreshRequested -= refreshRequested;
                                    if (summary) { workspace.IsPlaylistSummaryKeywordSearchHelpOpen = false; }
                                    else { filters.IsKeywordSearchHelpOpen = false; }
                                }
                            }
                        });

                        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        EventHandler closedHandler = (_, _) => closed.TrySetResult();
                        window.Closed += closedHandler;
                        try
                        {
                            window.Close();
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(window.CloseCompletion, "search-language.close-completion");
                            if (!closed.Task.IsCompleted) { window.Close(); }
                            TestUiDispatcherHost.AwaitPresentationOnDispatcher(closed.Task, "search-language.closed");
                        }
                        finally { window.Closed -= closedHandler; }
                        int normalNotifications = 0, summaryNotifications = 0, summaryTextNotifications = 0;
                        filters.PropertyChanged += (_, args) =>
                        {
                            if (args.PropertyName is nameof(filters.KeywordSearchWarningText) or nameof(filters.HasKeywordSearchWarning) or nameof(filters.KeywordSearchHelpText))
                            { normalNotifications++; }
                        };
                        workspace.PropertyChanged += (_, args) =>
                        {
                            if (args.PropertyName is nameof(workspace.PlaylistSummaryKeywordSearchWarningText) or nameof(workspace.HasPlaylistSummaryKeywordSearchWarning) or nameof(workspace.PlaylistSummaryKeywordSearchHelpText))
                            { summaryNotifications++; }
                            if (args.PropertyName is nameof(workspace.GridHeaderText) or nameof(workspace.PlaylistSummaryText))
                            { summaryTextNotifications++; }
                        };
                        ResourceService.Current.ChangeCulture("ja-JP");
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.AreEqual(0, normalNotifications);
                        Assert.AreEqual(0, summaryNotifications);
                        Assert.AreEqual(0, summaryTextNotifications);
                    });
            }
            finally
            {
                ResourceService.Current.ChangeCulture(previousCulture?.Name ?? "en-US");
                Resources.Culture = previousCulture;
            }
        });
    }

    /// <summary>未生成のお気に入りの選択・強調・Enter対象を一致させ、言語往復でも同じ行・ボタンの説明を更新して入力とscrollを保ち、区画横断と折返しで選択が残留しないことを確認します。</summary>
    /// <remarks>実ListBoxの選択と再利用したcontainerのBinding・言語変更通知の接続を検査します。候補計算と古い入力拒否はSearchのownerテスト、辞書整合はLocalizationResourceParityTestsが担います。</remarks>
    [TestMethod]
    public void SearchEditor_LargeFavoritesVirtualizeOffViewportRows()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                TestSettingsFactory.CreateIsolatedSettings(),
                (windowTest, viewModel, window) =>
                {
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    const int favoriteCount = 32;
                    for (int index = 0; index < favoriteCount; index++)
                    {
                        Assert.IsTrue(owner.TryAddFavorite($"title:favorite-{index}").Succeeded);
                    }
                    Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory("artist:history").Succeeded);

                    SearchChrome chrome = GetSearchChrome(window, "KeywordSearchEditor");
                    WithPresentedMainWindow(windowTest, window, () =>
                    {
                        CultureInfo? previousCulture = Resources.Culture;
                        try
                        {
                            ResourceService.Current.ChangeCulture("ja-JP");
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            PrepareSearchEditor(chrome, owner, string.Empty);
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            MaterializeMainWindow(window);

                            DependencyObject popupChild = chrome.SuggestionPopup.Child
                                ?? throw new AssertFailedException("The search assistance popup has no rendered child.");
                            int realizedFavorites = FindVisualDescendants<Border>(popupChild)
                                .Count(border => border.Tag is KeywordSearchPresentationItem item
                                    && item.Kind == KeywordSearchPresentationItemKind.Favorite);
                            Assert.IsTrue(realizedFavorites > 0);
                            Assert.IsTrue(
                                realizedFavorites < favoriteCount,
                                $"The 5-row Favorites viewport materialized {realizedFavorites} of {favoriteCount} rows.");
                            Assert.AreEqual(favoriteCount, owner.SavedQueryOwner.Favorites.Count);

                            ListBox favorites = GetSectionRows(chrome.Editor, KeywordSearchPresentationSectionKind.Favorites);
                            object lastFavorite = favorites.Items[^1];
                            Assert.IsNull(favorites.ItemContainerGenerator.ContainerFromItem(lastFavorite));
                            for (int index = 0; index < favoriteCount; index++)
                            {
                                RaiseSearchKey(chrome.Editor, Key.Down);
                            }
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            AssertSelectedSearchRow(favorites, favoriteCount - 1);
                            ScrollViewer favoriteViewport = FindVisualDescendants<ScrollViewer>(favorites).Single();
                            double selectedOffset = favoriteViewport.VerticalOffset;
                            owner.UpdateCatalogSnapshot(new KeywordSearchCatalogSnapshot(owner.CatalogSnapshot.Revision + 1, []));
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            // スクロールで実体化した行の操作ボタンの名称は各行のLoadedで付くため、Backgroundまで消化する。
                            TestUiDispatcherHost.ProcessQueuedBackground();
                            Assert.AreSame(favorites, GetSectionRows(chrome.Editor, KeywordSearchPresentationSectionKind.Favorites));
                            Assert.AreEqual(selectedOffset, favoriteViewport.VerticalOffset, 0.01d);
                            AssertSelectedSearchRow(favorites, favoriteCount - 1);

                            Border favoriteRow = FindSavedRow(chrome.Editor, ((KeywordSearchPresentationItem)lastFavorite).Query);
                            Border historyRow = FindSavedRow(chrome.Editor, "artist:history");
                            Button removeFavorite = FindActionButton(favoriteRow, Resources.Keyword_search_remove_favorite_tooltip);
                            Button addFavorite = FindActionButton(historyRow, Resources.Keyword_search_add_favorite_tooltip);
                            Button deleteHistory = FindActionButton(historyRow, Resources.Keyword_search_delete_history_tooltip);
                            ListBox[] sections = FindVisualDescendants<ListBox>(popupChild).ToArray();
                            ScrollViewer[] viewports = sections.Select(section => FindVisualDescendants<ScrollViewer>(section).Single()).ToArray();
                            double[] offsets = viewports.Select(viewport => viewport.VerticalOffset).ToArray();
                            string inputBeforeCulture = chrome.TextBox.Text;
                            int caretBeforeCulture = chrome.TextBox.CaretIndex;
                            Assert.IsTrue(selectedOffset > 0d, "言語切替前にお気に入りの画面外行までscrollする。");
                            foreach (string culture in new[] { "en-US", "ja-JP" })
                            {
                                ResourceService.Current.ChangeCulture(culture);
                                TestUiDispatcherHost.ProcessQueuedPresentation();
                                Assert.AreEqual(culture, Resources.Culture?.Name);
                                foreach ((Button button, string text) in new[]
                                {
                                    (removeFavorite, Resources.Keyword_search_remove_favorite_tooltip),
                                    (addFavorite, Resources.Keyword_search_add_favorite_tooltip),
                                    (deleteHistory, Resources.Keyword_search_delete_history_tooltip)
                                })
                                {
                                    Assert.AreEqual(text, button.ToolTip);
                                    Assert.AreEqual(text, AutomationProperties.GetName(button));
                                }
                                CollectionAssert.AreEqual(sections, FindVisualDescendants<ListBox>(popupChild).ToArray());
                                Assert.AreSame(favoriteRow, FindSavedRow(chrome.Editor, ((KeywordSearchPresentationItem)lastFavorite).Query));
                                Assert.AreSame(historyRow, FindSavedRow(chrome.Editor, "artist:history"));
                                CollectionAssert.AreEqual(new[] { removeFavorite }, FindVisibleActionButtons(favoriteRow));
                                CollectionAssert.AreEqual(new[] { addFavorite, deleteHistory }, FindVisibleActionButtons(historyRow));
                                Assert.IsTrue(chrome.SuggestionPopup.IsOpen);
                                AssertSelectedSearchRow(favorites, favoriteCount - 1);
                                Assert.AreEqual(((KeywordSearchPresentationItem)lastFavorite).Query, ((KeywordSearchPresentationItem)favorites.SelectedItem).Query);
                                Assert.AreEqual(inputBeforeCulture, chrome.TextBox.Text);
                                Assert.AreEqual(caretBeforeCulture, chrome.TextBox.CaretIndex);
                                for (int index = 0; index < offsets.Length; index++)
                                {
                                    Assert.AreEqual(offsets[index], viewports[index].VerticalOffset, 0.01d);
                                }
                            }
                            Assert.IsTrue(chrome.Editor.HandleInputKey(Key.Tab, ModifierKeys.Shift));
                            Assert.AreSame(removeFavorite, FocusManager.GetFocusedElement(FocusManager.GetFocusScope(removeFavorite)));
                            RaiseSearchLostFocus(chrome.TextBox, removeFavorite);
                            Assert.IsTrue(chrome.Editor.HandleEditorKey(removeFavorite, Key.Tab, ModifierKeys.None));
                            RaiseSearchGotFocus(chrome.TextBox, removeFavorite);
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            Assert.AreSame(favorites, GetSectionRows(chrome.Editor, KeywordSearchPresentationSectionKind.Favorites));
                            AssertSelectedSearchRow(favorites, favoriteCount - 1);
                            Assert.IsTrue(chrome.SuggestionPopup.IsOpen);
                            Assert.AreEqual(inputBeforeCulture, chrome.TextBox.Text);
                            Assert.AreEqual(caretBeforeCulture, chrome.TextBox.CaretIndex);
                            RaiseSearchKey(chrome.Editor, Key.Enter);
                            Assert.AreEqual(((KeywordSearchPresentationItem)lastFavorite).Query, chrome.TextBox.Text);

                            PrepareSearchEditor(chrome, owner, string.Empty);
                            favorites = GetSectionRows(chrome.Editor, KeywordSearchPresentationSectionKind.Favorites);
                            RaiseSearchKey(chrome.Editor, Key.Up);
                            ListBox fields = GetSectionRows(chrome.Editor, KeywordSearchPresentationSectionKind.Fields);
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            AssertSelectedSearchRow(fields, fields.Items.Count - 1);
                            Assert.AreEqual(-1, favorites.SelectedIndex);
                            RaiseSearchKey(chrome.Editor, Key.Down);
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            favorites = GetSectionRows(chrome.Editor, KeywordSearchPresentationSectionKind.Favorites);
                            AssertSelectedSearchRow(favorites, 0);
                            Assert.AreEqual(-1, fields.SelectedIndex);
                            for (int index = 0; index < favoriteCount; index++)
                            {
                                RaiseSearchKey(chrome.Editor, Key.Down);
                            }
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            ListBox history = GetSectionRows(chrome.Editor, KeywordSearchPresentationSectionKind.History);
                            AssertSelectedSearchRow(history, 0);
                            Assert.AreEqual(-1, favorites.SelectedIndex);
                            RaiseSearchKey(chrome.Editor, Key.Up);
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            AssertSelectedSearchRow(favorites, favoriteCount - 1);
                            Assert.AreEqual(-1, history.SelectedIndex);
                            Assert.IsTrue(favorites.Items.Cast<object>().Count(item =>
                                favorites.ItemContainerGenerator.ContainerFromItem(item) != null) < favoriteCount);
                        }
                        finally
                        {
                            ResourceService.Current.ChangeCulture(previousCulture?.Name ?? "en-US");
                            Resources.Culture = previousCulture;
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                        }
                    });
                });
        });
    }

    /// <summary>候補を選んでいないEnterは入力語のまま履歴へ確定して候補を閉じます。未選択のTabは、項目名・値の前方一致に使う文字が1文字以上あるときだけ先頭候補で補完し、空欄・0文字・Esc後は処理せず文字列と保存済み候補を変えません。上下キーとCtrl+Spaceで選んだ候補は従来どおりEnter・Tabで適用します。通常検索と一覧検索の両方で確認します。</summary>
    /// <remarks>
    /// 判定は入力欄の共通キー経路の戻り値、文字列とcaret、Popupと所有者の開閉、履歴・お気に入り、保存状態の変更回数です。
    /// 未選択Enter後に同じ文字列・caretで再計算しても閉じたままで、文字列変更かCtrl+Spaceで再び開くことも確認します。
    /// 抑制条件の網羅はCoreの <c>KsaLifecycle_SuppressionReopensOnInputContextCatalogAndForceRefreshAndBlur</c> が担い、OSの物理的なTabフォーカス移動は対象外です。
    /// 補完したTabは履歴を確定せず（保存状態の変更が0回）、続くTabは通常のフォーカス移動へ任せ、離脱時に履歴へ一回だけ確定します。
    /// 離脱処理は既存の <c>LostKeyboardFocus</c> を模して確認します。補完の置換範囲とcaretの網羅は既存のCoreテストが担います。
    /// 誤り例は、未選択のEnterで先頭候補を適用すること、未選択のTabが補完しないこと、空欄・Esc後・0文字でTabが文字列を変えること、
    /// 選択済みTabが選択を無視して先頭候補を適用すること、Enter後も候補が開いたままになること、履歴確定の欠落や二重化です。
    /// </remarks>
    [TestMethod]
    public void SearchEditor_UnselectedEnterKeepsInputAndTabCompletesTypedPrefixWhileSelectedCandidateApplies()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                TestSettingsFactory.CreateIsolatedSettings(),
                (windowTest, viewModel, window) =>
                {
                    WithPresentedMainWindow(windowTest, window, () =>
                    {
                        foreach (bool summaryMode in new[] { false, true })
                        {
                            viewModel.PlaylistWorkspace.SetPlaylistSummaryMode(summaryMode);
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            SearchChrome chrome = GetSearchChrome(window, summaryMode
                                ? "PlaylistSummaryKeywordSearchEditor" : "KeywordSearchEditor");
                            KeywordSearchAssistanceOwner owner = summaryMode
                                ? viewModel.PlaylistWorkspace.PlaylistSummaryKeywordSearchAssistanceOwner
                                : viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                            string typed = summaryMode ? "f" : "t";
                            string favorite = summaryMode ? "name:fav" : "title:fav";
                            string history = summaryMode ? "name:hist" : "title:hist";
                            string label = summaryMode ? "一覧検索" : "通常検索";
                            Assert.IsTrue(owner.TryAddFavorite(favorite).Succeeded, label);
                            Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory(history).Succeeded, label);
                            int stateChanges = 0;
                            owner.SavedQueryOwner.StateChanged += (_, _) => stateChanges++;
                            UIElement chartList = GetNamedElement<UIElement>(window, "customTableView");

                            // 空欄で保存式・項目が出ている未選択のTabは処理せず、文字列と保存内容を変えない。
                            PrepareSearchEditor(chrome, owner, string.Empty);
                            Assert.IsTrue(chrome.SuggestionPopup.IsOpen, label);
                            string[] favoritesBefore = owner.SavedQueryOwner.Favorites.ToArray();
                            string[] historyBefore = owner.SavedQueryOwner.History.ToArray();
                            stateChanges = 0;
                            Assert.IsFalse(chrome.Editor.HandleEditorKey(chrome.TextBox, Key.Tab, ModifierKeys.None), label);
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            Assert.AreEqual(string.Empty, chrome.TextBox.Text, label);
                            CollectionAssert.AreEqual(favoritesBefore, owner.SavedQueryOwner.Favorites.ToArray(), label);
                            CollectionAssert.AreEqual(historyBefore, owner.SavedQueryOwner.History.ToArray(), label);
                            Assert.AreEqual(0, stateChanges, label);

                            // 項目名の前方一致に使う文字がある未選択のTabは先頭候補で補完し、履歴は確定しない。
                            PrepareSearchEditor(chrome, owner, typed);
                            Assert.IsTrue(chrome.SuggestionPopup.IsOpen, label);
                            Assert.IsTrue(chrome.Editor.Presentation.VisibleItems.Count >= 2, label);
                            string insertion = chrome.Editor.Presentation.VisibleItems[0].InsertionText;
                            string second = chrome.Editor.Presentation.VisibleItems[1].InsertionText;
                            Assert.AreNotEqual(insertion, second, label);
                            stateChanges = 0;
                            Assert.IsTrue(chrome.Editor.HandleEditorKey(chrome.TextBox, Key.Tab, ModifierKeys.None), label);
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            Assert.AreEqual(insertion, chrome.TextBox.Text, label);
                            Assert.AreEqual(insertion.Length, chrome.TextBox.CaretIndex, label);
                            Assert.AreEqual(0, stateChanges, label);

                            // 補完後は前方一致に使う文字が0文字になるため、続くTabは処理せず離脱時に履歴へ一回だけ確定する。
                            Assert.IsFalse(chrome.Editor.HandleEditorKey(chrome.TextBox, Key.Tab, ModifierKeys.None), label);
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            Assert.AreEqual(insertion, chrome.TextBox.Text, label);
                            Assert.AreEqual(insertion.Length, chrome.TextBox.CaretIndex, label);
                            Assert.AreEqual(0, stateChanges, label);
                            RaiseSearchLostFocus(chrome.TextBox, chartList);
                            chrome.Editor.ClearKeyboardFocusIfOutside(chartList);
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            Assert.AreEqual(insertion, chrome.TextBox.Text, label);
                            Assert.AreEqual(insertion, owner.SavedQueryOwner.History[0], label);
                            Assert.AreEqual(1, stateChanges, label);
                            Assert.IsTrue(owner.TryDeleteHistory(insertion).Succeeded, label);

                            // 候補を閉じた後（Esc後）の未選択Tabは候補が出ていない扱いで、文字列を変えない。
                            PrepareSearchEditor(chrome, owner, typed);
                            Assert.IsTrue(chrome.Editor.HandleInputKey(Key.Escape, ModifierKeys.None), label);
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            Assert.IsFalse(chrome.SuggestionPopup.IsOpen, label);
                            stateChanges = 0;
                            Assert.IsFalse(chrome.Editor.HandleEditorKey(chrome.TextBox, Key.Tab, ModifierKeys.None), label);
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            Assert.AreEqual(typed, chrome.TextBox.Text, label);
                            Assert.AreEqual(typed.Length, chrome.TextBox.CaretIndex, label);
                            Assert.AreEqual(0, stateChanges, label);

                            // 値の前方一致に使う文字がある未選択のTab（通常検索）は先頭の値候補で補完する。
                            if (!summaryMode)
                            {
                                RaiseSearchLostFocus(chrome.TextBox, chartList);
                                chrome.Editor.ClearKeyboardFocusIfOutside(chartList);
                                TestUiDispatcherHost.ProcessQueuedPresentation();
                                PrepareSearchEditor(chrome, owner, "clear:E");
                                Assert.IsTrue(chrome.SuggestionPopup.IsOpen, label);
                                Assert.AreEqual(
                                    KeywordSearchPresentationItemKind.Value,
                                    chrome.Editor.Presentation.VisibleItems[0].Kind,
                                    label);
                                string valueInsertion = chrome.Editor.Presentation.VisibleItems[0].InsertionText;
                                stateChanges = 0;
                                Assert.IsTrue(chrome.Editor.HandleEditorKey(chrome.TextBox, Key.Tab, ModifierKeys.None), label);
                                TestUiDispatcherHost.ProcessQueuedPresentation();
                                StringAssert.StartsWith(chrome.TextBox.Text, "clear:" + valueInsertion, label);
                                Assert.AreEqual(0, stateChanges, label);
                            }

                            // 次の区画へ持ち越さないよう、離脱で確定された履歴を元の内容へ戻す。
                            RaiseSearchLostFocus(chrome.TextBox, chartList);
                            chrome.Editor.ClearKeyboardFocusIfOutside(chartList);
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            foreach (string committed in owner.SavedQueryOwner.History.Except(historyBefore).ToArray())
                            {
                                Assert.IsTrue(owner.TryDeleteHistory(committed).Succeeded, label);
                            }
                            CollectionAssert.AreEqual(historyBefore, owner.SavedQueryOwner.History.ToArray(), label);

                            // 候補を選んでいないEnterは入力語のまま履歴へ確定し、候補を閉じて処理済みにする。
                            PrepareSearchEditor(chrome, owner, typed);
                            AssertSearchInsertionOffered(chrome, insertion, label);
                            stateChanges = 0;
                            Assert.IsTrue(chrome.Editor.HandleEditorKey(chrome.TextBox, Key.Enter, ModifierKeys.None), label);
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            Assert.AreEqual(typed, chrome.TextBox.Text, label);
                            Assert.AreEqual(typed.Length, chrome.TextBox.CaretIndex, label);
                            Assert.AreEqual(typed, owner.SavedQueryOwner.History[0], label);
                            CollectionAssert.DoesNotContain(owner.SavedQueryOwner.History.ToArray(), insertion, label);
                            Assert.AreEqual(1, stateChanges, label);
                            Assert.IsFalse(chrome.SuggestionPopup.IsOpen, label);
                            Assert.IsFalse(owner.Presentation.IsOpen, label);
                            Assert.IsFalse(owner.Refresh(typed, typed.Length).IsOpen, "同じ文字列・caret・版の再計算では閉じたまま。" + label);
                            Assert.IsTrue(owner.Refresh(typed[..^1], typed.Length - 1).IsOpen, "文字列の変更で再び開く。" + label);
                            owner.Refresh(typed, typed.Length);
                            owner.SuppressCurrentSnapshot();
                            Assert.IsFalse(owner.Presentation.IsOpen, label);
                            Assert.IsTrue(chrome.Editor.HandleInputKey(Key.Space, ModifierKeys.Control), label);
                            Assert.IsTrue(owner.Presentation.IsOpen, "Ctrl+Spaceで再び開く。" + label);
                            Assert.IsTrue(owner.TryDeleteHistory(typed).Succeeded, label);

                            // 上下キーで選んだ先頭以外の候補のEnter・Tabは、選択した候補を適用する（先頭候補の補完とは区別する）。
                            foreach (Key applyKey in new[] { Key.Enter, Key.Tab })
                            {
                                // 直前の操作で残る選択表示を持ち越さないよう、検索欄から離れてから入力し直す。
                                RaiseSearchLostFocus(chrome.TextBox, chartList);
                                chrome.Editor.ClearKeyboardFocusIfOutside(chartList);
                                TestUiDispatcherHost.ProcessQueuedPresentation();
                                PrepareSearchEditor(chrome, owner, typed);
                                SelectSearchInsertion(chrome.Editor, second);
                                Assert.IsTrue(chrome.Editor.HandleEditorKey(chrome.TextBox, applyKey, ModifierKeys.None), label + applyKey);
                                TestUiDispatcherHost.ProcessQueuedPresentation();
                                Assert.AreEqual(second, chrome.TextBox.Text, label + applyKey);
                                Assert.AreEqual(second.Length, chrome.TextBox.CaretIndex, label + applyKey);
                            }

                            // Ctrl+Spaceで開いて先頭が選択表示された状態のEnterは、その候補を適用する。
                            RaiseSearchLostFocus(chrome.TextBox, chartList);
                            chrome.Editor.ClearKeyboardFocusIfOutside(chartList);
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            PrepareSearchEditor(chrome, owner, string.Empty);
                            chrome.Editor.HandleInputKey(Key.Escape, ModifierKeys.None);
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            Assert.IsTrue(chrome.Editor.HandleInputKey(Key.Space, ModifierKeys.Control), label);
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            KeywordSearchPresentationItem first = chrome.Editor.Presentation.VisibleItems[0];
                            string expected = first.Query.Length > 0 ? first.Query : first.InsertionText;
                            Assert.IsTrue(chrome.Editor.HandleEditorKey(chrome.TextBox, Key.Enter, ModifierKeys.None), label);
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            Assert.AreEqual(expected, chrome.TextBox.Text, label);
                            Assert.AreEqual(expected.Length, chrome.TextBox.CaretIndex, label);
                        }
                    });
                });
        });
    }

    /// <summary>通常検索のお気に入りBと一覧検索の履歴Bを同入力再計算後のボタン往復でも選択し、現在modifierのTabで同Bを適用し、B消失後に別行を本人として復元せず、未選択のEnterは文字列を変えず履歴も増やしません。</summary>
    /// <remarks>実Popupのボタンと入力欄に接続した共通キー境界を使います。OSの物理フォーカスや修飾キー取得の成否は合否に含めません。</remarks>
    [TestMethod]
    public void SearchEditor_SavedRowSelectionSurvivesActionRoundTripAndClearsOnRemoval()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                TestSettingsFactory.CreateIsolatedSettings(),
                (windowTest, viewModel, window) =>
                {
                    WithPresentedMainWindow(windowTest, window, () =>
                    {
                        foreach (bool summaryMode in new[] { false, true })
                        {
                            viewModel.PlaylistWorkspace.SetPlaylistSummaryMode(summaryMode);
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            SearchChrome chrome = GetSearchChrome(window, summaryMode
                                ? "PlaylistSummaryKeywordSearchEditor" : "KeywordSearchEditor");
                            KeywordSearchAssistanceOwner owner = summaryMode
                                ? viewModel.PlaylistWorkspace.PlaylistSummaryKeywordSearchAssistanceOwner
                                : viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                            string queryA = summaryMode ? "name:A" : "title:A";
                            string queryB = summaryMode ? "name:B" : "title:B";
                            if (summaryMode)
                            {
                                Assert.IsTrue(owner.TryAddFavorite("name:mixed").Succeeded);
                                Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory(queryA).Succeeded);
                                Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory(queryB).Succeeded);
                            }
                            else
                            {
                                Assert.IsTrue(owner.TryAddFavorite(queryB).Succeeded);
                                Assert.IsTrue(owner.TryAddFavorite(queryA).Succeeded);
                                Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory("artist:mixed").Succeeded);
                            }
                            KeywordSearchPresentationSectionKind kind = summaryMode
                                ? KeywordSearchPresentationSectionKind.History
                                : KeywordSearchPresentationSectionKind.Favorites;
                            PrepareSearchEditor(chrome, owner, string.Empty);
                            SelectSearchQuery(chrome.Editor, queryB);
                            ListBox rows = GetSectionRows(chrome.Editor, kind);
                            int selected = rows.SelectedIndex;
                            AssertSelectedSearchRow(rows, selected);
                            owner.Focus(string.Empty, 0);
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            Assert.AreSame(rows, GetSectionRows(chrome.Editor, kind), "同表示の再通知は区画を再生成しない。");
                            Assert.IsTrue(chrome.Editor.HandleInputKey(Key.Space, ModifierKeys.Control));
                            // Ctrl+Spaceの既存の先頭選択からBへ移動し、新presentationと表示行の参照が異なるまま往復します。
                            int queryIndex = chrome.Editor.Presentation.VisibleItems.ToList().FindIndex(item => item.Query == queryB);
                            Assert.IsTrue(queryIndex > 0);
                            for (int index = 0; index < queryIndex; index++)
                            {
                                RaiseSearchKey(chrome.Editor, Key.Down);
                            }

                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            Assert.AreSame(rows, GetSectionRows(chrome.Editor, kind));
                            AssertSelectedSearchRow(rows, selected);

                            // Shiftを検索欄外で解放した後の通常Tabには、以前のShift押下を持ち越さない。
                            chrome.Editor.HandleEditorKey(chrome.TextBox, Key.LeftShift, ModifierKeys.Shift);
                            Assert.IsTrue(chrome.Editor.HandleEditorKey(chrome.TextBox, Key.Tab, ModifierKeys.Shift));
                            Button[] actions = FindVisibleActionButtons(FindSavedRow(chrome.Editor, queryB));
                            Assert.IsTrue(actions.Length > 0);
                            Assert.AreSame(actions[0], FocusManager.GetFocusedElement(FocusManager.GetFocusScope(actions[0])));
                            RaiseSearchLostFocus(chrome.TextBox, actions[0]);
                            for (int index = 0; index < actions.Length; index++)
                            {
                                if (index > 0)
                                {
                                    Assert.IsTrue(chrome.Editor.HandleEditorKey(actions[index], Key.Tab, ModifierKeys.Shift));
                                    Assert.IsTrue(chrome.Editor.HandleEditorKey(actions[index - 1], Key.Tab, ModifierKeys.None));
                                }
                                Assert.IsTrue(chrome.Editor.HandleEditorKey(actions[index], Key.Tab, ModifierKeys.None));
                            }
                            RaiseSearchGotFocus(chrome.TextBox, actions[^1]);
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            Assert.AreEqual(string.Empty, chrome.TextBox.Text);
                            Assert.IsTrue(chrome.SuggestionPopup.IsOpen);
                            rows = GetSectionRows(chrome.Editor, kind);
                            AssertSelectedSearchRow(rows, selected);
                            Assert.AreEqual(queryB, ((KeywordSearchPresentationItem)rows.SelectedItem).Query);
                            long revision = owner.CatalogSnapshot.Revision + 1;
                            owner.UpdateCatalogSnapshot(new KeywordSearchCatalogSnapshot(revision, []));
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            Assert.AreSame(rows, GetSectionRows(chrome.Editor, kind));
                            AssertSelectedSearchRow(rows, selected);
                            Assert.AreEqual(revision, chrome.Editor.Presentation.CatalogRevision);
                            Assert.AreEqual(revision, ((KeywordSearchPresentationItem)rows.SelectedItem).Identity.CatalogRevision);
                            Assert.IsTrue(chrome.Editor.HandleEditorKey(chrome.TextBox, Key.Tab, ModifierKeys.None));
                            Assert.AreEqual(queryB, chrome.TextBox.Text);

                            PrepareSearchEditor(chrome, owner, string.Empty);
                            SelectSearchQuery(chrome.Editor, queryB);
                            Assert.IsTrue((summaryMode
                                ? owner.TryDeleteHistory(queryB) : owner.TryRemoveFavorite(queryB)).Succeeded);
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            foreach (ListBox section in FindVisualDescendants<ListBox>(chrome.SuggestionPopup.Child))
                            {
                                Assert.AreEqual(-1, section.SelectedIndex, "行消失は別候補への選択復元を行わない。");
                            }
                            string[] historyBeforeEnter = owner.SavedQueryOwner.History.ToArray();
                            Assert.IsTrue(chrome.Editor.HandleEditorKey(chrome.TextBox, Key.Enter, ModifierKeys.None));
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            Assert.AreEqual(string.Empty, chrome.TextBox.Text, "選択行が消えた後の未選択Enterは先頭候補を適用しない。");
                            Assert.IsFalse(chrome.SuggestionPopup.IsOpen);
                            CollectionAssert.AreEqual(historyBeforeEnter, owner.SavedQueryOwner.History.ToArray());
                        }
                    });
                });
        });
    }

    /// <summary>入力欄と所有Popupのボタン間移動は候補を保ち、行内ボタンから一覧・別検索への離脱は元だけを閉じ、履歴変更と閉鎖を重ねません。</summary>
    /// <remarks>Popupの別ツリーへ実接続したLostKeyboardFocusを発火し、保存状態と表示通知を観測します。履歴保存失敗やBlur冪等の網羅はownerテストが担います。</remarks>
    [TestMethod]
    public void SearchEditor_OwnedPopupFocusTransitionsEndOnlyTheDepartedEditor()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                TestSettingsFactory.CreateIsolatedSettings(),
                (windowTest, viewModel, window) =>
                {
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory("title:B").Succeeded);
                    SearchChrome normal = GetSearchChrome(window, "KeywordSearchEditor");
                    SearchChrome summary = GetSearchChrome(window, "PlaylistSummaryKeywordSearchEditor");
                    WithPresentedMainWindow(windowTest, window, () =>
                    {
                        PrepareSearchEditor(normal, owner, string.Empty);
                        Button[] actions = FindVisibleActionButtons(FindSavedRow(normal.Editor, "title:B"));
                        Assert.AreEqual(2, actions.Length);
                        int stateChanges = 0;
                        int closes = 0;
                        owner.SavedQueryOwner.StateChanged += (_, _) => stateChanges++;
                        owner.PresentationChanged += (_, _) =>
                        {
                            if (!owner.Presentation.IsOpen)
                            {
                                closes++;
                            }
                        };
                        RaiseSearchLostFocus(normal.TextBox, actions[0]);
                        RaiseSearchLostFocus(actions[0], actions[1]);
                        RaiseSearchLostFocus(actions[1], normal.TextBox);
                        Assert.IsTrue(normal.SuggestionPopup.IsOpen);
                        Assert.AreEqual(0, stateChanges);
                        Assert.AreEqual(0, closes);

                        UIElement chartList = GetNamedElement<UIElement>(window, "customTableView");
                        RaiseSearchLostFocus(actions[0], chartList);
                        RaiseSearchLostFocus(normal.TextBox, chartList);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.IsFalse(normal.SuggestionPopup.IsOpen);
                        Assert.AreEqual(1, closes);
                        Assert.AreEqual(0, stateChanges);

                        PrepareSearchEditor(normal, owner, string.Empty);
                        actions = FindVisibleActionButtons(FindSavedRow(normal.Editor, "title:B"));
                        KeywordSearchAssistanceOwner summaryOwner = viewModel.PlaylistWorkspace.PlaylistSummaryKeywordSearchAssistanceOwner;
                        summaryOwner.Focus(string.Empty, 0);
                        RaiseSearchLostFocus(actions[0], summary.TextBox);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.IsFalse(normal.SuggestionPopup.IsOpen);
                        Assert.IsTrue(summaryOwner.Presentation.IsOpen);
                        Assert.AreEqual(2, closes);
                        Assert.AreEqual(0, stateChanges);

                        PrepareSearchEditor(normal, owner, "title:typed");
                        RaiseSearchLostFocus(normal.TextBox, chartList);
                        normal.Editor.ClearKeyboardFocusIfOutside(chartList);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.AreEqual(1, stateChanges);
                        Assert.AreEqual("title:typed", owner.SavedQueryOwner.History[0]);
                        Assert.AreEqual("title:typed", normal.TextBox.Text);
                    });
                });
        });
    }

    /// <summary>画面外行への保留focusは外部離脱・Unload・owner交換で失効し、キューを処理しても古いボタンの論理focusと候補を復活させません。</summary>
    /// <remarks>実Popupの仮想化とDispatcherキューを使い、OSの物理focus取得成功には依存しません。</remarks>
    [TestMethod]
    public void SearchEditor_PendingActionFocusIsInvalidatedAtEachLifetimeBoundary()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                TestSettingsFactory.CreateIsolatedSettings(),
                (windowTest, viewModel, window) =>
                {
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    for (int index = 0; index < 32; index++)
                    {
                        Assert.IsTrue(owner.TryAddFavorite($"title:{index}").Succeeded);
                    }
                    SearchChrome chrome = GetSearchChrome(window, "KeywordSearchEditor");
                    WithPresentedMainWindow(windowTest, window, () =>
                    {
                        foreach (string boundary in new[] { "outside", "unload", "owner" })
                        {
                            chrome.Editor.Configure(owner);
                            PrepareSearchEditor(chrome, owner, string.Empty);
                            ListBox rows = GetSectionRows(chrome.Editor, KeywordSearchPresentationSectionKind.Favorites);
                            Assert.IsNull(rows.ItemContainerGenerator.ContainerFromIndex(31));
                            // 未生成行を論理選択するが、レイアウトのキューは終端まで処理しない。
                            for (int index = 0; index < 32; index++)
                            {
                                RaiseSearchKey(chrome.Editor, Key.Down);
                            }

                            Assert.IsTrue(chrome.Editor.HandleInputKey(Key.Tab, ModifierKeys.Shift));
                            if (boundary == "outside")
                            {
                                RaiseSearchLostFocus(chrome.TextBox, window);
                            }
                            else if (boundary == "unload")
                            {
                                chrome.Editor.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent, chrome.Editor));
                            }
                            else
                            {
                                chrome.Editor.Configure(viewModel.PlaylistWorkspace.PlaylistSummaryKeywordSearchAssistanceOwner);
                            }
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            Assert.IsFalse(owner.Presentation.IsOpen, boundary);
                            Assert.IsFalse(chrome.SuggestionPopup.IsOpen, boundary);
                            foreach (Button action in FindVisualDescendants<Button>(rows).Where(button => button.IsTabStop))
                            {
                                Assert.AreNotSame(action, FocusManager.GetFocusedElement(FocusManager.GetFocusScope(action)), boundary);
                            }
                        }
                    });
                });
        });
    }

    /// <summary>お気に入りと履歴の画面外行をscrollで表示し、同じ描画surfaceのDataContext変更にも全文tooltipと操作名を追従させます。</summary>
    [TestMethod]
    public void SearchEditor_SavedRowTooltipsAndAccessibilityFollowRecycledDataContext()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                TestSettingsFactory.CreateIsolatedSettings(),
                (windowTest, viewModel, window) =>
                {
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    const int favoriteCount = 32;
                    for (int index = 0; index < favoriteCount; index++)
                    {
                        Assert.IsTrue(owner.TryAddFavorite($"title:favorite-{index}").Succeeded);
                    }
                    const int historyCount = 6;
                    for (int index = 0; index < historyCount; index++)
                    {
                        Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory($"artist:history-{index}").Succeeded);
                    }

                    SearchChrome chrome = GetSearchChrome(window, "KeywordSearchEditor");
                    WithPresentedMainWindow(windowTest, window, () =>
                    {
                        PrepareSearchEditor(chrome, owner, string.Empty);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        MaterializeMainWindow(window);

                        foreach (KeywordSearchPresentationSectionKind sectionKind in new[]
                        {
                            KeywordSearchPresentationSectionKind.Favorites,
                            KeywordSearchPresentationSectionKind.History
                        })
                        {
                            KeywordSearchPresentationSection section = chrome.Editor.Presentation.Sections
                                .Single(candidate => candidate.Kind == sectionKind);
                            KeywordSearchPresentationItem firstItem = section.Items[0];
                            KeywordSearchPresentationItem lastItem = section.Items[^1];
                            ListBox rows = GetSectionRows(chrome.Editor, sectionKind);
                            rows.ScrollIntoView(firstItem);
                            MaterializeElement(rows, rows.ActualWidth, rows.ActualHeight);
                            rows.ScrollIntoView(lastItem);
                            MaterializeElement(rows, rows.ActualWidth, rows.ActualHeight);
                            // 実体化した行の操作ボタンの名称は各行のLoadedで付くため、Backgroundまで消化する。
                            TestUiDispatcherHost.ProcessQueuedBackground();
                            Border lastRow = FindSavedRow(chrome.Editor, lastItem.Query);
                            Button lastApply = FindApplyButton(lastRow);
                            Assert.IsNotNull(
                                rows.ItemContainerGenerator.ContainerFromItem(lastItem),
                                $"ScrollIntoView must realize the initially off-viewport {sectionKind} row.");
                            Assert.AreEqual(lastItem.Query, lastRow.ToolTip?.ToString());
                            Assert.AreEqual(lastItem.Query, lastApply.ToolTip?.ToString());
                            Assert.AreEqual(lastItem.DisplayText, AutomationProperties.GetName(lastApply));

                            Button[] lastActions = FindVisibleActionButtons(lastRow);
                            Assert.IsTrue(lastActions.Length > 0);
                            foreach (Button action in lastActions)
                            {
                                string actionName = AutomationProperties.GetName(action);
                                Assert.IsFalse(string.IsNullOrWhiteSpace(actionName));
                                Assert.AreEqual(actionName, action.ToolTip?.ToString());
                                Assert.AreNotEqual(lastItem.Query, actionName);
                            }

                            // runtimeがtemplateを再生成しても、同じsurfaceを再利用する入力を固定します。
                            // Loaded時の行状態だけでtooltipを保持する実装を検出します。
                            lastRow.SetCurrentValue(FrameworkElement.DataContextProperty, firstItem);
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            Assert.AreSame(lastApply, FindApplyButton(lastRow));
                            Assert.AreEqual(firstItem.Query, lastRow.ToolTip?.ToString());
                            Assert.AreEqual(firstItem.Query, lastApply.ToolTip?.ToString());
                            Assert.AreEqual(firstItem.DisplayText, AutomationProperties.GetName(lastApply));
                            foreach (Button action in FindVisibleActionButtons(lastRow))
                            {
                                string actionName = AutomationProperties.GetName(action);
                                Assert.IsFalse(string.IsNullOrWhiteSpace(actionName));
                                Assert.AreEqual(actionName, action.ToolTip?.ToString());
                                Assert.AreNotEqual(firstItem.Query, actionName);
                            }
                        }
                    });
                });
        });
    }

    /// <summary>保存済み検索には全文tooltipと識別名を出し、通常・サマリーのフィールド候補と値候補には保存クエリtooltipを混入させません。</summary>
    [TestMethod]
    public void SearchEditor_QueryTooltipsAreLimitedToSavedRows()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                TestSettingsFactory.CreateIsolatedSettings(),
                (windowTest, viewModel, window) =>
                {
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    Assert.IsTrue(owner.TryAddFavorite("title:favorite").Succeeded);
                    Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory("artist:history").Succeeded);

                    SearchChrome chrome = GetSearchChrome(window, "KeywordSearchEditor");
                    SearchChrome summary = GetSearchChrome(window, "PlaylistSummaryKeywordSearchEditor");
                    WithPresentedMainWindow(windowTest, window, () =>
                    {
                        PrepareSearchEditor(chrome, owner, string.Empty);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        MaterializeMainWindow(window);

                        Border favoriteRow = FindSavedRow(chrome.Editor, "title:favorite");
                        Button favoriteApply = FindApplyButton(favoriteRow);
                        Assert.AreEqual("title:favorite", favoriteApply.ToolTip?.ToString());
                        Assert.AreEqual(
                            "title:favorite",
                            AutomationProperties.GetName(favoriteApply));

                        chrome.TextBox.Text = "tit";
                        chrome.TextBox.CaretIndex = chrome.TextBox.Text.Length;
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Border fieldRow = FindRowForItemKind(
                            chrome.Editor,
                            KeywordSearchPresentationItemKind.Field);
                        Button fieldApply = FindApplyButton(fieldRow);
                        Assert.IsNull(fieldRow.ToolTip);
                        Assert.IsNull(fieldApply.ToolTip);

                        chrome.TextBox.Text = "clear:N";
                        chrome.TextBox.CaretIndex = chrome.TextBox.Text.Length;
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Border valueRow = FindRowForItemKind(
                            chrome.Editor,
                            KeywordSearchPresentationItemKind.Value);
                        Button valueApply = FindApplyButton(valueRow);
                        Assert.IsNull(valueRow.ToolTip);
                        Assert.IsNull(valueApply.ToolTip);

                        viewModel.PlaylistWorkspace.SetPlaylistSummaryMode(true);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        KeywordSearchAssistanceOwner summaryOwner =
                            viewModel.PlaylistWorkspace.PlaylistSummaryKeywordSearchAssistanceOwner;
                        PrepareSearchEditor(summary, summaryOwner, string.Empty);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Border summaryFieldRow = FindRowForItemKind(
                            summary.Editor,
                            KeywordSearchPresentationItemKind.Field);
                        Button summaryFieldApply = FindApplyButton(summaryFieldRow);
                        Assert.IsNull(summaryFieldRow.ToolTip);
                        Assert.IsNull(summaryFieldApply.ToolTip);
                    });
                });
        });
    }

    /// <summary>異なる幅へ変更した通常・サマリー入力面をpopup開放前に捕捉し、長い保存クエリでpopupを広げず全文と省略表示を保ちます。</summary>
    [TestMethod]
    public void SearchEditor_SavedQueryPopupWidthIsBoundToPreOpenSurface()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                TestSettingsFactory.CreateIsolatedSettings(),
                (windowTest, viewModel, window) =>
                {
                    const string longQuery =
                        "title:This-is-a-deliberately-long-saved-query-that-must-remain-intact-" +
                        "0123456789abcdefghijklmnopqrstuvwxyz";
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    Assert.IsTrue(owner.TryAddFavorite(longQuery).Succeeded);

                    SearchChrome chrome = GetSearchChrome(window, "KeywordSearchEditor");
                    SearchChrome summary = GetSearchChrome(window, "PlaylistSummaryKeywordSearchEditor");
                    WithPresentedMainWindow(windowTest, window, () =>
                    {
                        MaterializeMainWindow(window);
                        Grid surface = GetEditorSurface(chrome.Editor);
                        double baselineWidth = surface.ActualWidth;
                        Assert.IsTrue(baselineWidth > 0d);
                        surface.SetCurrentValue(
                            FrameworkElement.WidthProperty,
                            baselineWidth * 0.75d);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        MaterializeMainWindow(window);
                        double preOpenWidth = surface.ActualWidth;
                        Assert.IsTrue(preOpenWidth > 0d);

                        PrepareSearchEditor(chrome, owner, string.Empty);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        MaterializeMainWindow(window);

                        Border popupChild = chrome.SuggestionPopup.Child as Border
                            ?? throw new AssertFailedException("The search assistance popup has no outer Border.");
                        Assert.AreEqual(preOpenWidth, popupChild.ActualWidth, 0.51d);
                        Border longRow = FindSavedRow(chrome.Editor, longQuery);
                        Button longApply = FindApplyButton(longRow);
                        TextBlock longLabel = longApply.Content as TextBlock
                            ?? throw new AssertFailedException("The saved-query apply button has no text label.");
                        Assert.AreEqual(longQuery, longLabel.Text);
                        Assert.AreEqual(TextWrapping.NoWrap, longLabel.TextWrapping);
                        Assert.AreEqual(TextTrimming.CharacterEllipsis, longLabel.TextTrimming);
                        Assert.IsTrue(longLabel.ActualWidth <= popupChild.ActualWidth + 0.51d);

                        chrome.Editor.AssistancePopupControl.IsOpen = false;
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.IsFalse(chrome.SuggestionPopup.IsOpen);
                        viewModel.PlaylistWorkspace.SetPlaylistSummaryMode(true);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        MaterializeMainWindow(window);
                        Grid summarySurface = GetEditorSurface(summary.Editor);
                        double summaryBaselineWidth = summarySurface.ActualWidth;
                        Assert.IsTrue(summaryBaselineWidth > 0d);
                        summarySurface.SetCurrentValue(
                            FrameworkElement.WidthProperty,
                            summaryBaselineWidth * 0.875d);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        MaterializeMainWindow(window);
                        double summaryPreOpenWidth = summarySurface.ActualWidth;
                        Assert.IsTrue(summaryPreOpenWidth > 0d);
                        const double layoutRoundingTolerance = 0.51d;
                        Assert.IsTrue(
                            Math.Abs(preOpenWidth - summaryPreOpenWidth) > layoutRoundingTolerance,
                            $"Normal and summary editor fixture widths must differ beyond layout rounding: normal={preOpenWidth}, summary={summaryPreOpenWidth}.");
                        KeywordSearchAssistanceOwner summaryOwner =
                            viewModel.PlaylistWorkspace.PlaylistSummaryKeywordSearchAssistanceOwner;
                        PrepareSearchEditor(summary, summaryOwner, string.Empty);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        MaterializeMainWindow(window);
                        Border summaryPopupChild = summary.SuggestionPopup.Child as Border
                            ?? throw new AssertFailedException("The summary assistance popup has no outer Border.");
                        Assert.AreEqual(summaryPreOpenWidth, summaryPopupChild.ActualWidth, 0.51d);
                    });
                });
        });
    }

    /// <summary>お気に入り・履歴・値・フィールドの各少数候補は件数増減へviewport高さを追従させ、最大高さまでの空白を常に確保しません。</summary>
    [TestMethod]
    public void SearchEditor_ShortSectionsSizeToContentForSavedValuesAndFields()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                TestSettingsFactory.CreateIsolatedSettings(),
                (windowTest, viewModel, window) =>
                {
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    Assert.IsTrue(owner.TryAddFavorite("title:favorite").Succeeded);
                    Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory("artist:history").Succeeded);
                    SearchChrome chrome = GetSearchChrome(window, "KeywordSearchEditor");

                    WithPresentedMainWindow(windowTest, window, () =>
                    {
                        PrepareSearchEditor(chrome, owner, string.Empty);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        double[] oneSavedRowHeights = GetSectionViewportHeights(chrome.Editor);
                        Assert.IsTrue(oneSavedRowHeights.Length >= 2);
                        Assert.IsTrue(oneSavedRowHeights[0] > 0d && oneSavedRowHeights[1] > 0d);

                        Assert.IsTrue(owner.TryAddFavorite("title:favorite-two").Succeeded);
                        Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory("artist:history-two").Succeeded);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        double[] twoSavedRowHeights = GetSectionViewportHeights(chrome.Editor);
                        Assert.IsTrue(twoSavedRowHeights[0] > oneSavedRowHeights[0]);
                        Assert.IsTrue(twoSavedRowHeights[1] > oneSavedRowHeights[1]);

                        PrepareSearchEditor(chrome, owner, "clear:NP");
                        double oneValueHeight = GetSectionViewportHeights(chrome.Editor).Single();
                        Assert.IsTrue(oneValueHeight > 0d);

                        PrepareSearchEditor(chrome, owner, "clear:N");
                        double twoValueHeight = GetSectionViewportHeights(chrome.Editor).Single();
                        Assert.IsTrue(twoValueHeight > oneValueHeight);

                        PrepareSearchEditor(chrome, owner, "cl");
                        double oneFieldHeight = GetSectionViewportHeights(chrome.Editor).Single();
                        Assert.IsTrue(oneFieldHeight > 0d);

                        PrepareSearchEditor(chrome, owner, "c");
                        double twoFieldHeight = GetSectionViewportHeights(chrome.Editor).Single();
                        Assert.IsTrue(twoFieldHeight > oneFieldHeight);
                    });
                });
        });
    }

    /// <summary>実IME開始・更新イベント中のEnterでは候補適用と履歴確定を抑止し、IME確定後は上下キーで選んだ候補をEnter・Tabで適用します。</summary>
    [TestMethod]
    public void SearchEditor_ActiveImeCompositionRoutedEventsDoNotApplyOrCommitHistory()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                TestSettingsFactory.CreateIsolatedSettings(),
                (windowTest, viewModel, window) =>
                {
                    SearchChrome chrome = GetSearchChrome(window, "KeywordSearchEditor");
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    int historyCountBefore = owner.SavedQueryOwner.History.Count;

                    WithPresentedMainWindow(windowTest, window, () =>
                    {
                        PrepareSearchEditor(chrome, owner, "tit");
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        RaiseSearchKey(chrome.Editor, Key.Down);
                        string textBeforeComposition = chrome.TextBox.Text;
                        int caretBeforeComposition = chrome.TextBox.CaretIndex;

                        TextComposition composition = new(
                            InputManager.Current,
                            chrome.TextBox,
                            "title");
                        RaiseCompositionEvent(
                            chrome.TextBox,
                            composition,
                            TextCompositionManager.PreviewTextInputStartEvent);
                        RaiseCompositionEvent(
                            chrome.TextBox,
                            composition,
                            TextCompositionManager.PreviewTextInputUpdateEvent);
                        RaiseSearchKey(chrome.Editor, Key.Enter);
                        TestUiDispatcherHost.ProcessQueuedPresentation();

                        Assert.AreEqual(textBeforeComposition, chrome.TextBox.Text);
                        Assert.AreEqual(caretBeforeComposition, chrome.TextBox.CaretIndex);
                        Assert.AreEqual(historyCountBefore, owner.SavedQueryOwner.History.Count);

                        RaiseCompositionEvent(
                            chrome.TextBox,
                            new TextComposition(InputManager.Current, chrome.TextBox, "title"),
                            TextCompositionManager.TextInputEvent);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.AreEqual(textBeforeComposition + "title", chrome.TextBox.Text);
                        Assert.AreEqual(historyCountBefore, owner.SavedQueryOwner.History.Count);

                        // IME確定後は途中編集からEnter・Tabを適用し、実TextBoxのcaretとBinding先まで確認します。
                        chrome.TextBox.SetCurrentValue(TextBox.TextProperty, "title:alpha");
                        chrome.TextBox.CaretIndex = 3;
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        RaiseSearchKey(chrome.Editor, Key.Down);
                        RaiseSearchKey(chrome.Editor, Key.Enter);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.AreEqual("title:alpha", chrome.TextBox.Text);
                        Assert.AreEqual("title:".Length, chrome.TextBox.CaretIndex);
                        // 外側の500ms遅延Bindingを既存接続テストと同じ方法で確定します。
                        BindingOperations.GetBindingExpression(chrome.Editor, KeywordSearchEditor.TextProperty)?.UpdateSource();
                        Assert.AreEqual(chrome.TextBox.Text, viewModel.ChartFilters.KeywordFilter);

                        chrome.TextBox.SetCurrentValue(TextBox.TextProperty, "clear:F|EXH|NP title:x");
                        chrome.TextBox.CaretIndex = "clear:F|E".Length;
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        RaiseSearchKey(chrome.Editor, Key.Down);
                        RaiseSearchKey(chrome.Editor, Key.Tab);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.AreEqual("clear:F|EC|NP title:x", chrome.TextBox.Text);
                        Assert.AreEqual("clear:F|EC".Length, chrome.TextBox.CaretIndex);
                        // 外側の500ms遅延Bindingを既存接続テストと同じ方法で確定します。
                        BindingOperations.GetBindingExpression(chrome.Editor, KeywordSearchEditor.TextProperty)?.UpdateSource();
                        Assert.AreEqual(chrome.TextBox.Text, viewModel.ChartFilters.KeywordFilter);
                    });
                });
        });
    }

    /// <summary>Simple/Canonical入力の適用後Padding変更を実文字へ一回反映し、枠厚変更とEditableTextBlock表示/編集の文字位置整合を確認します。</summary>
    [TestMethod]
    public void SimpleTextBox_TemplateChromeFollowsEffectiveBorderThickness()
    {
        WithSimpleTextBoxStyles(() => TestUiDispatcherHost.RunWindowTest(scope =>
        {
            var host = new StackPanel();
            host.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/BeMusicSeeker;component/Themes/Simple Styles.xaml", UriKind.RelativeOrAbsolute)
            });
            host.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/BeMusicSeeker;component/Themes/CanonicalControls.xaml", UriKind.RelativeOrAbsolute)
            });
            host.Resources.Add(typeof(TextBox), (Style)host.FindResource("SimpleTextBox"));
            var simple = new TextBox { Text = "padding", Height = 40, BorderThickness = new Thickness(3) };
            var canonical = new TextBox
            {
                Text = "padding",
                Height = 40,
                BorderThickness = new Thickness(1),
                Style = (Style)host.FindResource("App.Canonical.TextBoxStyle")
            };
            var editable = new EditableTextBlock { Text = "editable", DisplayText = "editable", Width = 220, Height = 30 };
            host.Children.Add(simple);
            host.Children.Add(canonical);
            host.Children.Add(editable);
            var window = new Window { Width = 520, Height = 220, Content = host };
            scope.ShowAndWaitForContentRendered(window);
            foreach (TextBox input in new[] { simple, canonical })
            {
                AssertTextBoxChrome(input, input.BorderThickness);
                var viewer = (ScrollViewer)input.Template.FindName("PART_ContentHost", input);
                input.Padding = new Thickness(2, 3, 4, 5);
                window.UpdateLayout();
                Rect before = input.GetRectFromCharacterIndex(0);
                input.Padding = new Thickness(7, 3, 4, 5);
                window.UpdateLayout();
                Rect after = input.GetRectFromCharacterIndex(0);
                Assert.AreEqual(input.Padding, viewer.Padding);
                Assert.AreEqual(5d, after.Left - before.Left, 0.25d, "適用後のPadding変更は実文字へ一度だけ反映する。");
                input.BorderThickness = new Thickness(4);
                window.UpdateLayout();
                AssertTextBoxChrome(input, new Thickness(4));
            }
            TextBlock display = FindVisualDescendants<TextBlock>(editable).Single();
            Rect displayCharacter = display.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            Point displayPoint = display.TranslatePoint(displayCharacter.TopLeft, editable);
            editable.IsInEditMode = true;
            TestUiDispatcherHost.ProcessQueuedPresentation();
            window.UpdateLayout();
            TextBox editor = FindVisualDescendants<TextBox>(editable).Single();
            AssertTextBoxChrome(editor, new Thickness(0));
            Rect editCharacter = editor.GetRectFromCharacterIndex(0);
            Point editPoint = editor.TranslatePoint(editCharacter.TopLeft, editable);
            Assert.AreEqual(displayPoint.X, editPoint.X, 0.25d, "表示/編集を切り替えても実文字の横位置を合わせる。");
            Assert.AreEqual(displayPoint.Y, editPoint.Y, 0.25d, "表示/編集を切り替えても実文字の縦位置を合わせる。");
        }));
    }

    private static T GetNamedElement<T>(MainWindow window, string name)
        where T : class
    {
        object? element = window.FindName(name);
        Assert.IsInstanceOfType(element, typeof(T), name);
        return (T)element!;
    }




    private static void AssertFilterAffordanceMaterialized(SearchChrome chrome)
    {
        Assert.IsNotNull(chrome.FilterButton.DropDownContextMenu);
        Assert.IsTrue(
            chrome.FilterButton.ApplyTemplate(),
            "The search filter affordance must materialize its control template.");
        Assert.IsNotNull(chrome.FilterButton.Template);
    }

    private static void AssertFilterAffordanceRendered(SearchChrome chrome)
    {
        MaterializeElement(chrome.FilterButton, 20d, 20d);
        FrameworkElement[] descendants = FindVisualDescendants<FrameworkElement>(chrome.FilterButton).ToArray();
        FrameworkElement? renderedDescendant = descendants
            .FirstOrDefault(element => element.ActualWidth > 0d && element.ActualHeight > 0d);
        Assert.IsNotNull(
            renderedDescendant,
            "The search filter affordance must expose a rendered descendant with non-zero bounds.");
        Rect descendantBounds = VisualTreeHelper.GetDescendantBounds(chrome.FilterButton);
        Assert.IsFalse(descendantBounds.IsEmpty);
        Assert.IsTrue(descendantBounds.Width > 0d && descendantBounds.Height > 0d);
    }

    private static MenuItem GetFilterMenuItem(ContextMenu menu, bool checkedState)
    {
        MenuItem[] items = menu.Items.OfType<MenuItem>().ToArray();
        foreach (MenuItem item in items)
        {
            BindingOperations.GetBindingExpression(item, MenuItem.IsCheckedProperty)?.UpdateTarget();
        }

        MenuItem? match = items.FirstOrDefault(item => item.IsChecked == checkedState);
        Assert.IsNotNull(match, $"The filter menu must expose a bound item with IsChecked={checkedState}.");
        return match!;
    }

    private static void SetFilterMenuItemChecked(MenuItem item, bool checkedState)
    {
        item.SetCurrentValue(MenuItem.IsCheckedProperty, checkedState);
        BindingOperations.GetBindingExpression(item, MenuItem.IsCheckedProperty)?.UpdateSource();
        TestUiDispatcherHost.ProcessQueuedPresentation();
    }

    private static TextBox? GetInstalledEditor(CustomTableView table)
        => table.Children
            .OfType<Canvas>()
            .SelectMany(canvas => canvas.Children.OfType<TextBox>())
            .SingleOrDefault();

    private static SearchChrome GetSearchChrome(MainWindow window, string editorName)
    {
        string suffix = string.Equals(editorName, "KeywordSearchEditor", StringComparison.Ordinal)
            ? string.Empty
            : "PlaylistSummary";
        KeywordSearchEditor editor = GetNamedElement<KeywordSearchEditor>(
            window,
            editorName);
        return new SearchChrome(
            editor,
            editor.InputTextBoxControl,
            GetNamedElement<Border>(window, $"{suffix}KeywordSearchComposite"),
            GetNamedElement<Grid>(window, $"{suffix}KeywordSearchWarningSlot"),
            GetNamedElement<ToggleButton>(window, $"{suffix}KeywordSearchHelpButton"),
            GetNamedElement<Grid>(window, $"{suffix}KeywordSearchHelpSlot"),
            GetNamedElement<AppFontIcon>(window, $"{suffix}KeywordSearchClearIcon"),
            GetNamedElement<Grid>(window, $"{suffix}KeywordSearchClearSlot"),
            editor.AssistancePopupControl,
            GetNamedElement<Popup>(window, $"{suffix}KeywordSearchHelpPopup"));
    }


    private static void WithPresentedMainWindow(TestWindowPresentationScope presentationScope, MainWindow window, Action action)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(presentationScope);
        object dataContext = window.DataContext;
        RoutedEventHandler suppressStartupActivation = (_, _) =>
        {
            window.Dispatcher.BeginInvoke(
                DispatcherPriority.Render,
                new Action(() =>
                {
                    window.Left = SystemParameters.VirtualScreenLeft
                        + (SystemParameters.VirtualScreenWidth * 4d)
                        + 4096d;
                    window.Top = SystemParameters.VirtualScreenTop
                        + (SystemParameters.VirtualScreenHeight * 4d)
                        + 4096d;
                }));
            window.Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(() => window.DataContext = null));
        };
        try
        {
            window.Loaded += suppressStartupActivation;
            presentationScope.ShowAndWaitForContentRendered(window);
            window.DataContext = dataContext;
            TestUiDispatcherHost.ProcessQueuedPresentation();
            action();
        }
        finally
        {
            window.Loaded -= suppressStartupActivation;
            // 親Windowは外側harnessが実shutdown/Closedを回収した後に同scopeで片付けます。
            window.DataContext = dataContext;
        }
    }

    private static void PrepareSearchEditor(
        SearchChrome chrome,
        KeywordSearchAssistanceOwner owner,
        string text)
    {
        chrome.TextBox.SetCurrentValue(TextBox.TextProperty, text ?? string.Empty);
        chrome.TextBox.CaretIndex = chrome.TextBox.Text.Length;
        RaiseSearchGotFocus(chrome.TextBox);
        Assert.AreEqual(owner.Context, chrome.Editor.Presentation.Context);
        TestUiDispatcherHost.ProcessQueuedPresentation();
    }

    private static void RaiseSearchKey(
        KeywordSearchEditor editor,
        Key key,
        ModifierKeys modifiers = ModifierKeys.None,
        Key imeProcessedKey = Key.None)
        => editor.HandleInputKey(key, modifiers, imeProcessedKey);

    private static void AssertSelectedSearchRow(ListBox rows, int index)
    {
        rows.UpdateLayout();
        Assert.AreEqual(index, rows.SelectedIndex);
        Assert.AreSame(rows.Items[index], rows.SelectedItem);
        var container = rows.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
        Assert.IsNotNull(container, "選択した画面外の行はscroll後に生成される。");
        Assert.IsTrue(container.IsSelected);
        Border row = FindVisualDescendants<Border>(container)
            .Single(border => border.Tag is KeywordSearchPresentationItem);
        Assert.AreEqual(row.FindResource("App.ControlBackgroundActiveBrush"), row.Background);
        foreach (ListBoxItem other in rows.Items.Cast<object>()
            .Select(item => rows.ItemContainerGenerator.ContainerFromItem(item)).OfType<ListBoxItem>())
        {
            Assert.AreEqual(ReferenceEquals(other, container), other.IsSelected,
                "再利用された行に以前の選択強調を残さない。");
        }
    }

    private static void SelectSearchQuery(KeywordSearchEditor editor, string query)
    {
        int index = editor.Presentation.VisibleItems.ToList().FindIndex(item => item.Query == query);
        Assert.IsTrue(index >= 0,
            $"{editor.Presentation.Context} の候補に {query} が必要: {string.Join("|", editor.Presentation.VisibleItems.Select(item => item.Query))}");
        for (int step = 0; step <= index; step++)
        {
            RaiseSearchKey(editor, Key.Down);
        }

        TestUiDispatcherHost.ProcessQueuedPresentation();
    }

    private static void AssertSearchInsertionOffered(SearchChrome chrome, string insertion, string label)
    {
        Assert.IsTrue(chrome.SuggestionPopup.IsOpen, label);
        Assert.IsTrue(
            chrome.Editor.Presentation.VisibleItems.Any(item => item.InsertionText == insertion),
            $"{label}: {insertion} が候補に必要: {string.Join("|", chrome.Editor.Presentation.VisibleItems.Select(item => item.InsertionText))}");
    }

    private static void SelectSearchInsertion(KeywordSearchEditor editor, string insertion)
    {
        int index = editor.Presentation.VisibleItems.ToList().FindIndex(item => item.InsertionText == insertion);
        Assert.IsTrue(index >= 0, insertion);
        for (int step = 0; step <= index; step++)
        {
            RaiseSearchKey(editor, Key.Down);
        }

        TestUiDispatcherHost.ProcessQueuedPresentation();
    }

    private static void RaiseSearchLostFocus(UIElement source, IInputElement destination)
        => source.RaiseEvent(new KeyboardFocusChangedEventArgs(
            Keyboard.PrimaryDevice, Environment.TickCount, source, destination)
        {
            RoutedEvent = Keyboard.LostKeyboardFocusEvent
        });

    private static void RaiseSearchGotFocus(UIElement destination, IInputElement? source = null)
        => destination.RaiseEvent(new KeyboardFocusChangedEventArgs(
            Keyboard.PrimaryDevice, Environment.TickCount, source, destination)
        {
            RoutedEvent = Keyboard.GotKeyboardFocusEvent
        });

    private static void AssertCandidateRowLimit(ListBox rows, int limit)
    {
        // 実Popupの候補を使い、上限ちょうどは完全表示、次の一行は独立scrollへ回す。
        IEnumerable originalSource = rows.ItemsSource;
        object[] items = rows.Items.Cast<object>().ToArray();
        Assert.IsTrue(items.Length > limit);
        ScrollViewer viewer = FindVisualDescendants<ScrollViewer>(rows).Single();
        double? maximumHeight = null;
        foreach (int count in new[] { limit - 1, limit, limit + 1 })
        {
            rows.ItemsSource = items.Take(count).ToArray();
            TestUiDispatcherHost.ProcessQueuedPresentation();
            rows.UpdateLayout();
            var presenter = (ScrollContentPresenter)viewer.Template.FindName("PART_ScrollContentPresenter", viewer);
            if (count <= limit)
            {
                Assert.AreEqual(0d, viewer.ScrollableHeight, 0.01d, "上限以内の候補は全行を表示する。");
                var last = (ListBoxItem)rows.ItemContainerGenerator.ContainerFromIndex(count - 1);
                Assert.IsNotNull(last);
                Rect bounds = last.TransformToAncestor(presenter).TransformBounds(new Rect(last.RenderSize));
                Assert.IsTrue(bounds.Top >= -0.25d && bounds.Bottom <= presenter.ActualHeight + 0.25d);
                if (maximumHeight.HasValue)
                {
                    Assert.IsTrue(rows.ActualHeight > maximumHeight.Value, "少数候補は件数に応じて自然に伸長する。");
                }
                maximumHeight = rows.ActualHeight;
            }
            else
            {
                Assert.IsTrue(viewer.ScrollableHeight > 0d, "上限を一行越えるとscrollする。");
                Assert.AreEqual(maximumHeight.GetValueOrDefault(), rows.ActualHeight, 0.25d);
            }
        }
        rows.ItemsSource = originalSource;
        TestUiDispatcherHost.ProcessQueuedPresentation();
        rows.UpdateLayout();
    }

    private static double[] GetSectionViewportHeights(KeywordSearchEditor editor)
    {
        DependencyObject popupChild = editor.AssistancePopupControl.Child
            ?? throw new AssertFailedException("The search assistance popup has no rendered child.");
        double[] heights = FindVisualDescendants<ScrollViewer>(popupChild)
            .Select(viewport => viewport.ActualHeight)
            .Where(height => height > 0d)
            .ToArray();
        Assert.IsTrue(heights.Length > 0,
            $"候補は実viewportを持つ: popup={editor.AssistancePopupControl.IsOpen}, presentation={editor.Presentation.IsOpen}, items={editor.Presentation.VisibleItems.Count}。");
        return heights;
    }

    private static void RaiseCompositionEvent(
        TextBox textBox,
        TextComposition composition,
        RoutedEvent routedEvent)
    {
        textBox.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
        {
            RoutedEvent = routedEvent
        });
    }

    private static Border FindSavedRow(KeywordSearchEditor editor, string query)
    {
        DependencyObject popupChild = editor.AssistancePopupControl.Child
            ?? throw new AssertFailedException("The search assistance popup has no rendered child.");
        Border? row = FindVisualDescendants<Border>(popupChild)
            .SingleOrDefault(border => border.Tag is KeywordSearchPresentationItem item
                && item.Kind is KeywordSearchPresentationItemKind.Favorite or KeywordSearchPresentationItemKind.History
                && string.Equals(item.Query, query, StringComparison.Ordinal));
        Assert.IsNotNull(
            row,
            $"Saved row '{query}' was not materialized; popupOpen={editor.AssistancePopupControl.IsOpen}, presentationOpen={editor.Presentation.IsOpen}, presentationItems={string.Join("|", editor.Presentation.VisibleItems.Select(item => $"{item.Kind}:{item.Query}"))}, visualItems={string.Join("|", FindVisualDescendants<Border>(popupChild).Select(border => border.Tag).OfType<KeywordSearchPresentationItem>().Select(item => $"{item.Kind}:{item.Query}"))}.");
        return row!;
    }

    private static ListBox GetSectionRows(
        KeywordSearchEditor editor,
        KeywordSearchPresentationSectionKind sectionKind)
    {
        DependencyObject popupChild = editor.AssistancePopupControl.Child
            ?? throw new AssertFailedException("The search assistance popup has no rendered child.");
        ListBox[] rows = FindVisualDescendants<ListBox>(popupChild).ToArray();
        KeywordSearchPresentationSection[] sections = editor.Presentation.Sections
            .Where(section => section.IsVisible)
            .ToArray();
        int sectionIndex = Array.FindIndex(
            sections,
            section => section.Kind == sectionKind);
        Assert.IsTrue(sectionIndex >= 0, $"The assistance section {sectionKind} is not visible.");
        Assert.IsTrue(rows.Length > sectionIndex, $"The assistance section {sectionKind} has no ListBox.");
        return rows[sectionIndex];
    }

    private static Grid GetEditorSurface(KeywordSearchEditor editor)
    {
        Grid? surface = FindVisualDescendants<Grid>(editor)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, "editorSurface", StringComparison.Ordinal));
        Assert.IsNotNull(surface, "The keyword-search editor surface must be materialized.");
        return surface!;
    }

    private static Button FindApplyButton(Border row)
        => FindVisualDescendants<Button>(row)
            .Single(button => button.Content is TextBlock);

    private static Border FindRowForItemKind(
        KeywordSearchEditor editor,
        KeywordSearchPresentationItemKind itemKind)
    {
        DependencyObject popupChild = editor.AssistancePopupControl.Child
            ?? throw new AssertFailedException("The search assistance popup has no rendered child.");
        return FindVisualDescendants<Border>(popupChild)
            .First(border => border.Tag is KeywordSearchPresentationItem item
                && item.Kind == itemKind);
    }

    private static Button[] FindVisibleActionButtons(Border row)
        => FindVisualDescendants<Button>(row)
            .Where(button => button.Focusable
                && button.IsTabStop
                && button.Visibility == Visibility.Visible
                && button.IsVisible
                && button.IsEnabled)
            .ToArray();

    private static Button FindActionButton(Border row, string accessibleName)
    {
        return FindVisualDescendants<Button>(row)
            .Single(button => string.Equals(
                AutomationProperties.GetName(button),
                accessibleName,
                StringComparison.Ordinal));
    }

    private static void AssertTextBoxChrome(TextBox textBox, Thickness expectedThickness)
    {
        Assert.AreEqual(expectedThickness, textBox.BorderThickness);
        Assert.AreEqual(expectedThickness, GetTemplateBorder(textBox).BorderThickness);
    }

    private static Border GetTemplateBorder(TextBox textBox)
    {
        textBox.ApplyTemplate();
        Assert.IsNotNull(textBox.Template);
        Border? border = FindVisualDescendants<Border>(textBox).FirstOrDefault();
        Assert.IsNotNull(border, $"{textBox.Name} did not materialize a template Border.");
        return border!;
    }

    private static Point GetRelativeOrigin(FrameworkElement element, FrameworkElement ancestor)
        => element.TranslatePoint(new Point(0d, 0d), ancestor);

    private static void AssertSamePoint(Point expected, Point actual)
    {
        Assert.AreEqual(expected.X, actual.X, 0.01d);
        Assert.AreEqual(expected.Y, actual.Y, 0.01d);
    }


    private static void AssertSlotChildCentered(
        FrameworkElement slot,
        FrameworkElement child,
        FrameworkElement ancestor)
    {
        Point slotOrigin = GetRelativeOrigin(slot, ancestor);
        Point childOrigin = GetRelativeOrigin(child, ancestor);
        var slotCenter = new Point(
            slotOrigin.X + slot.ActualWidth / 2d,
            slotOrigin.Y + slot.ActualHeight / 2d);
        var childCenter = new Point(
            childOrigin.X + child.ActualWidth / 2d,
            childOrigin.Y + child.ActualHeight / 2d);
        const double devicePixelTolerance = 0.51d;
        Assert.AreEqual(slotCenter.X, childCenter.X, devicePixelTolerance);
        Assert.AreEqual(slotCenter.Y, childCenter.Y, devicePixelTolerance);
    }


    private static void MaterializeMainWindow(MainWindow window)
    {
        window.Width = 1200d;
        window.Height = 800d;
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0d, 0d, window.Width, window.Height));
        window.UpdateLayout();
        TestUiDispatcherHost.ProcessQueuedPresentation();
    }

    private static void MaterializeElement(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0d, 0d, width, height));
        element.UpdateLayout();
        TestUiDispatcherHost.ProcessQueuedPresentation();
    }

    private static void RaiseMouseLeftButtonDown(UIElement element)
    {
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent
        });
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }
            foreach (T descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed record SearchChrome(
        KeywordSearchEditor Editor,
        TextBox TextBox,
        Border Outer,
        Grid WarningSlot,
        ToggleButton HelpButton,
        Grid HelpSlot,
        AppFontIcon ClearIcon,
        Grid ClearSlot,
        Popup SuggestionPopup,
        Popup HelpPopup)
    {
        internal Grid Layout => (Grid)Outer.Child;

        internal DropDownMenuButton FilterButton => Layout.Children.OfType<DropDownMenuButton>().Single();

        internal AppFontIcon WarningContent => WarningSlot.Children.OfType<AppFontIcon>().Single();
    }

    private static CustomTableView CreateEditableTable(CustomTableColumn column, double width, double height)
    {
        var table = new CustomTableView
        {
            Width = (int)width,
            Height = height,
            HeaderHeight = 0d,
            RowHeight = 60d,
            Columns = [column],
            ItemsSource = new List<object> { new object() }
        };
        return table;
    }

    private static CustomTableColumn CreateScrollBarContractColumn(double width)
    {
        var layout = new CustomTableColumnSettings.ColumnLayout
        {
            Width = (int)width,
            Visibility = Visibility.Visible
        };
        return new CustomTableColumn(
            "Contract",
            "Contract",
            layout,
            fallbackOrder: 0,
            sortMemberPath: null,
            alignment: TextAlignment.Left,
            textSelector: _ => string.Empty,
            editPropertyName: "Contract");
    }

    /// <summary>
    /// 一覧用の通常状態を実矩形で確認します。背景は不透明な7 DIPの長手帯で外端を表の内縁に固定し、つまみ3 DIPの両側に各2 DIPを残します。
    /// </summary>
    private static void AssertListScrollBarGeometry(
        CustomTableView table,
        ScrollBar bar,
        Border background,
        Border thumb,
        double tableWidth,
        double tableHeight)
    {
        Assert.IsInstanceOfType<SolidColorBrush>(background.Background);
        Assert.AreEqual((byte)255, ((SolidColorBrush)background.Background).Color.A, "背景の色自体が不透明。");
        Assert.AreEqual(1d, GetEffectiveOpacity(background, table), 0.0001d, "祖先を含む実効Opacityが1。");
        Rect barBounds = bar.TransformToAncestor(table).TransformBounds(new Rect(bar.RenderSize));
        Rect backgroundBounds = background.TransformToAncestor(table).TransformBounds(new Rect(background.RenderSize));
        Rect thumbBounds = thumb.TransformToAncestor(table).TransformBounds(new Rect(thumb.RenderSize));
        if (bar.Orientation == Orientation.Vertical)
        {
            Assert.AreEqual(16d, barBounds.Width, 0.01d, "入力領域は16 DIPのまま。");
            Assert.AreEqual(7d, backgroundBounds.Width, 0.25d);
            Assert.AreEqual(3d, thumbBounds.Width, 0.25d);
            Assert.AreEqual(tableWidth, backgroundBounds.Right, 0.25d, "Windowなしでは外枠補正0で、背景は表の内縁に接する。");
            Assert.AreEqual(barBounds.Height, backgroundBounds.Height, 0.25d, "背景は長手方向の帯。");
            Assert.AreEqual(2d, thumbBounds.Left - backgroundBounds.Left, 0.25d);
            Assert.AreEqual(2d, backgroundBounds.Right - thumbBounds.Right, 0.25d);
        }
        else
        {
            Assert.AreEqual(16d, barBounds.Height, 0.01d, "入力領域は16 DIPのまま。");
            Assert.AreEqual(7d, backgroundBounds.Height, 0.25d);
            Assert.AreEqual(3d, thumbBounds.Height, 0.25d);
            Assert.AreEqual(tableHeight, backgroundBounds.Bottom, 0.25d, "Windowなしでは外枠補正0で、背景は表の内縁に接する。");
            Assert.AreEqual(barBounds.Width, backgroundBounds.Width, 0.25d, "背景は長手方向の帯。");
            Assert.AreEqual(2d, thumbBounds.Top - backgroundBounds.Top, 0.25d);
            Assert.AreEqual(2d, backgroundBounds.Bottom - thumbBounds.Bottom, 0.25d);
        }
    }

    /// <summary>透明な入力領域の実hit-testがバー自身へ届き、縦横の交点が縦バーへ属することを確認します。</summary>
    private static void AssertListScrollBarHitTesting(CustomTableView table, ScrollBar vertical, ScrollBar horizontal)
    {
        Rect verticalBounds = vertical.TransformToAncestor(table).TransformBounds(new Rect(vertical.RenderSize));
        Rect horizontalBounds = horizontal.TransformToAncestor(table).TransformBounds(new Rect(horizontal.RenderSize));
        // 縦バーの背景左側にある透明部分と、横バーの背景上側にある透明部分。
        Assert.AreSame(vertical, FindScrollBarAt(table, new Point(verticalBounds.Left + 1d, verticalBounds.Top + 8d)));
        Assert.AreSame(horizontal, FindScrollBarAt(table, new Point(horizontalBounds.Left + 8d, horizontalBounds.Top + 1d)));
        // 横バーの右端は縦バーと重ならず、交点は縦バーへ割り当てる。
        Assert.AreEqual(verticalBounds.Left, horizontalBounds.Right, 0.01d);
        Assert.AreSame(vertical, FindScrollBarAt(table, new Point(verticalBounds.Left + 8d, verticalBounds.Bottom - 8d)));
    }

    private static ScrollBar? FindScrollBarAt(UIElement root, Point point)
    {
        HitTestResult? result = VisualTreeHelper.HitTest(root, point);
        for (DependencyObject? current = result?.VisualHit; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ScrollBar bar)
            {
                return bar;
            }
        }
        return null;
    }

    private static double GetEffectiveOpacity(UIElement element, UIElement ancestor)
    {
        double opacity = 1d;
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement visual)
            {
                opacity *= visual.Opacity;
            }
            if (ReferenceEquals(current, ancestor))
            {
                break;
            }
        }
        return opacity;
    }

    private static void MaterializeScrollBarContractTable(CustomTableView table, double width, double height)
    {
        table.Measure(new Size(width, height));
        table.Arrange(new Rect(0d, 0d, width, height));
        table.UpdateLayout();
    }

    private static void AssertScrollBarPresentation(ScrollBar scrollBar, Orientation orientation)
    {
        scrollBar.ApplyTemplate();
        Assert.IsNotNull(scrollBar.Template);
        var track = (Track)scrollBar.Template.FindName("PART_Track", scrollBar);
        Assert.IsNotNull(track);
        Assert.AreEqual(orientation, scrollBar.Orientation);
        Assert.AreEqual(orientation, track.Orientation);
    }

    private static void FlushResourceUpdates(CustomTableView table)
    {
        table.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
        table.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
    }

    private static void MaterializeTable(CustomTableView table, double width, double height)
    {
        table.Measure(new Size(width, height));
        table.Arrange(new Rect(0d, 0d, width, height));
        table.UpdateLayout();
        table.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
    }

    private static void RaiseKey(CustomTableView table, Key key)
        => table.HandleKeyDown(key, ModifierKeys.None);

}
