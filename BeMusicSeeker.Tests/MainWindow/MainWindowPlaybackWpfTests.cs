using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
// 構成済みMainWindowの共有Resourcesを表示待機と終了回収まで所有します。
[DoNotParallelize]
public sealed class MainWindowPlaybackWpfTests
{
    /// <summary>停止中のCtrl+AからShift下で主行の題名・字幕・作者と表示譜面を更新し、実開始後の同じ選択操作では現在曲の表示を保つことを確認し、標準terminalへの主行通知漏れを検出します。</summary>
    /// <remarks>集合・主行・基点の条件差はSelectionModel、通知回数と入力内の現在セルはWindowなしNavigationへ分担し、このケースはcompiled Bindingと標準再生接続を確認します。</remarks>
    [TestMethod]
    public void RangeSelection_UpdatesStoppedHeaderAndKeepsPlayingChartThroughStandardTerminal()
    {
        string directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "main-range-selection-" + Guid.NewGuid().ToString("N"))).FullName;
        string firstPath = Path.Combine(directory, "first.bms");
        string lastPath = Path.Combine(directory, "last.bms");
        File.WriteAllBytes(firstPath, []);
        File.WriteAllBytes(lastPath, []);
        try
        {
            MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
                MainWindowViewModelTestFactory.CreateIsolatedSettings(),
                (scope, viewModel, window) =>
                {
                    var player = new PlaybackPanelViewModelTests.FakeBmsPlayer();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.PlaybackPanel.ReplacePlayerAsync(player), "range-replace-player");
                    ChartFile first = ChartTestValues.Empty() with { Path = firstPath, RawTitle = "先頭の題名", Subtitle = "先頭の字幕", RawArtist = "先頭の作者" };
                    ChartFile last = ChartTestValues.Empty() with { Path = lastPath, RawTitle = "末尾の題名", Subtitle = "末尾の字幕", RawArtist = "末尾の作者" };
                    viewModel.MainChartList.Rows = new List<object> { first, last };
                    viewModel.MainChartList.SelectedIndex = -1;
                    MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
                    var table = (CustomTableView)window.FindName("customTableView");
                    var panel = (PlaybackPanelView)window.FindName("playbackPanelView");
                    var title = (TextBlock)panel.FindName("gridBMSPlayerControlsTitle");
                    var subtitle = (TextBlock)panel.FindName("gridBMSPlayerControlsSubtitle");
                    var artist = (TextBlock)panel.FindName("gridBMSPlayerControlsArtist");
                    void AssertDisplayed(ChartFile chart)
                    {
                        Assert.AreSame(chart, viewModel.PlaybackPanel.DisplayedChart);
                        Assert.AreEqual(chart.RawTitle, viewModel.PlaybackPanel.PlayerHeaderTitle);
                        Assert.AreEqual(chart.Subtitle, viewModel.PlaybackPanel.PlayerHeaderSubtitle);
                        Assert.AreEqual(chart.Artist, viewModel.PlaybackPanel.PlayerHeaderArtist);
                        // 実開始Taskの終端と、背景通知からのBinding反映は別に観測します。
                        var displayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        var text = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
                        void Observe(object? sender, EventArgs args)
                        {
                            if (title.Text == chart.RawTitle && subtitle.Text == chart.Subtitle && artist.Text == chart.Artist)
                            {
                                displayed.TrySetResult();
                            }
                        }
                        foreach (TextBlock block in new[] { title, subtitle, artist }) { text.AddValueChanged(block, Observe); }
                        try
                        {
                            Observe(null, EventArgs.Empty);
                            TestUiDispatcherHost.AwaitPresentationOnDispatcher(displayed.Task, "range-header-binding");
                            Assert.AreEqual(chart.RawTitle, title.Text);
                            Assert.AreEqual(chart.Subtitle, subtitle.Text);
                            Assert.AreEqual(chart.Artist, artist.Text);
                        }
                        finally
                        {
                            foreach (TextBlock block in new[] { title, subtitle, artist }) { text.RemoveValueChanged(block, Observe); }
                        }
                    }
                    try
                    {
                        table.SelectRowsByPredicate(row => ReferenceEquals(row, first));
                        AssertDisplayed(first);
                        Assert.IsTrue(table.HandleKeyDown(Key.A, ModifierKeys.Control));
                        Assert.IsTrue(table.HandleKeyDown(Key.Down, ModifierKeys.Shift));
                        Assert.AreEqual(1, table.SelectedIndex);
                        CollectionAssert.AreEqual(new object[] { first, last }, table.GetSelectedRowsSnapshot().ToArray());
                        Assert.IsNull(viewModel.PlaybackPanel.NowPlayingChart);
                        AssertDisplayed(last);

                        TestUiDispatcherHost.AwaitTaskOnDispatcher(window.RequestPlaybackFromTable(0, first), "range-start-first-terminal");
                        Assert.AreSame(first, viewModel.PlaybackPanel.NowPlayingChart);
                        AssertDisplayed(first);
                        Assert.IsTrue(table.HandleKeyDown(Key.A, ModifierKeys.Control));
                        Assert.IsTrue(table.HandleKeyDown(Key.Down, ModifierKeys.Shift));
                        Assert.AreEqual(1, table.SelectedIndex);
                        Assert.AreSame(first, viewModel.PlaybackPanel.NowPlayingChart);
                        AssertDisplayed(first);
                        Assert.AreEqual(1, player.Commands.Count(command => command == "PlayStart:" + firstPath));
                    }
                    finally
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.PlaybackPanel.StopPlayback(), "range-stop-cleanup");
                    }
                });
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>実再生パネルの操作可否Bindingと表の選択・Enterが構成済み再生入口へ行とindexを一回渡すことを確認し、画面の状態参照や再生操作接続の欠落を検出します。</summary>
    /// <remarks>再生状態の判断はPlaybackPanelの下位ケース、Loadedの表とcompiled Binding・編集維持への配送は実WPFで分担します。</remarks>
    [TestMethod]
    public void PlaybackControls_BindPanelStateAndCapabilitiesThroughPlaybackOwner()
    {
        object? selectedRow = null;
        var activatedRows = new List<(int RowIndex, object Row)>();
        var playbackTerminal = new MainWindowPlaybackTerminal(
            row => selectedRow = row,
            (rowIndex, row, _) =>
            {
                activatedRows.Add((rowIndex, row));
                return Task.CompletedTask;
            });

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (scope, viewModel, window) =>
            {
                MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
                var panel = (PlaybackPanelView)window.FindName("playbackPanelView");
                Assert.IsNotNull(panel);
                Assert.AreSame(viewModel.PlaybackPanel, panel.DataContext);
                var caption = (StackPanel)window.FindName("windowCaptionPanel");
                foreach (Button button in caption.Children.OfType<Button>())
                {
                    button.ApplyTemplate();
                    var surface = (Border)button.Template.FindName("border", button);
                    Assert.AreEqual(Colors.Transparent, ((SolidColorBrush)surface.Background).Color);
                    Assert.IsNull(surface.Effect);
                }

                IReadOnlyList<Button> seekButtons = FindVisualChildren<Button>(panel)
                    .Where(button => BindingOperations.GetBindingBase(button, UIElement.IsEnabledProperty) is Binding binding
                        && string.Equals(binding.Path?.Path, "CanSeek", StringComparison.Ordinal))
                    .ToArray();
                Assert.IsTrue(seekButtons.Count > 0);
                Assert.IsTrue(seekButtons.All(button => button.IsEnabled == viewModel.PlaybackPanel.CanSeek));

                object row = new object();
                var table = (CustomTableView)window.FindName("customTableView");
                table.ItemsSource = new List<object> { new object(), row };
                table.SelectRowsByPredicate(candidate => ReferenceEquals(candidate, row));

                Assert.AreEqual(1, table.SelectedIndex);
                Assert.AreSame(row, selectedRow);

                RaiseKey(table, Key.Return);

                Assert.AreEqual(1, activatedRows.Count);
                Assert.AreEqual(1, activatedRows[0].RowIndex);
                Assert.AreSame(row, activatedRows[0].Row);
            },
            playbackTerminal: playbackTerminal);
    }

    /// <summary>標準構成の両再生入口では競合拒否の実終端まで面・保存値を変えず、解放後の次の明示入力だけが実面と保存値を切り替えます。</summary>
    /// <remarks>実BMIIDXView2015の設定・process・window操作を既存gatewayで代替し、実開始と対象pathを観測します。compiled表のキーから同じ入口への行identity転送は既存ケース、後段L競合と入力時状態の条件表はownerケースに委ねます。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PlaybackSurface_ChangesOnlyAfterAcceptedExplicitInput(bool playButton)
    {
        string directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "main-playback-surface-" + Guid.NewGuid().ToString("N"))).FullName;
        string path = Path.Combine(directory, "chart.bms");
        string executable = Path.Combine(directory, "BMIIDXView2015.exe");
        File.WriteAllBytes(path, []);
        File.WriteAllBytes(executable, []);
        try
        {
            TestUiDispatcherHost.RunWindowTest(scope =>
            {
                Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
                {
                    values.UsePlayerBMIIDXView = true;
                    values.UsePlayeruBMplay = false;
                    values.UsePlayerLR2body = false;
                    values.BMIIDXViewPath = executable;
                    values.PlayerPanelState = PlayerPanelState.TITLE_LARGE;
                });
                var lifetime = new MainWindowPresentationTestHarness.PresentationApplicationLifetime();
                var composition = new ApplicationComposition(
                    settingsEditSession: new NoOpSettingsEditSession(settings),
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: lifetime,
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog());
                MainWindowViewModel viewModel = composition.CreateMainWindowViewModelForTest();
                var ownership = new MainWindowTestLifetime(viewModel, lifetime.ShutdownRequested.Task);
                var processGateway = new ExternalPlayerProcessGatewayTests.RecordingExternalPlayerProcessGateway();
                processGateway.Session.KeepRunning = true;
                processGateway.Session.MainWindowHandle = new ExternalWindowHandle(new IntPtr(21));
                int starts = 0;
                processGateway.Session.BeforeStart = () => starts++;
                var player = new BMIIDXView2015(executable,
                    new ExternalPlayerProcessGatewayTests.RecordingPlayerSettingsGateway(), processGateway);
                ownership.Run(() =>
                {
                    try
                    {
                        MainWindowPresentationTestHarness.PrepareConstructorOnlyShell(viewModel);
                        viewModel.StartupUpdateWorkflow.NotifyClosing();
                        viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(false);
                        MainWindow window = ownership.CreateWindow(() => new MainWindow(viewModel));
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.PlaybackPanel.ReplacePlayerAsync(player), "surface-replace-player");
                        ChartFile chart = ChartTestValues.Empty() with { Path = path, RawTitle = "明示入力の対象" };
                        viewModel.MainChartList.Rows = new List<object> { chart };
                        viewModel.MainChartList.SelectedIndex = 0;
                        MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
                        var panelView = (PlaybackPanelView)window.FindName("playbackPanelView");
                        viewModel.PlaybackPanel.AttachWindowHost(new ExternalPlayerProcessGatewayTests.RecordingExternalPlayerWindowHost(
                            new ExternalWindowHandle(panelView.PlayerHostHandle)));
                        var table = (CustomTableView)window.FindName("customTableView");
                        table.SelectRowsByPredicate(row => ReferenceEquals(row, chart));
                        Assert.AreEqual(0, viewModel.MainChartList.SelectedIndex);
                        Assert.AreEqual(PlayerPanelState.TITLE_LARGE, panelView.EffectivePlayerPanelState);
                        Assert.AreEqual(PlayerPanelState.TITLE_LARGE, settings.PlayerPanelState);
                        Task Request() => playButton
                            ? panelView.RequestPlaybackFromPlayButton()
                            : window.RequestPlaybackFromTable(0, chart);
                        Assert.IsTrue(composition.OperationAdmission.TryEnter(out IDisposable lease));
                        try
                        {
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(Request(), "surface-rejected-request-terminal");
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            Assert.AreEqual(PlayerPanelState.TITLE_LARGE, panelView.EffectivePlayerPanelState);
                            Assert.AreEqual(PlayerPanelState.TITLE_LARGE, settings.PlayerPanelState);
                            Assert.IsNull(viewModel.PlaybackPanel.NowPlayingChart);
                            Assert.AreEqual(0, starts);
                            Assert.IsFalse(processGateway.Session.Started);
                        }
                        finally { lease.Dispose(); }
                        Assert.AreEqual(0, starts);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(Request(), "surface-accepted-request-terminal");
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.IsTrue(panelView.EffectivePlayerPanelState.HasFlag(PlayerPanelState.BMS_PLAYER));
                        Assert.IsTrue(settings.PlayerPanelState.HasFlag(PlayerPanelState.BMS_PLAYER));
                        Assert.AreSame(chart, viewModel.PlaybackPanel.NowPlayingChart);
                        Assert.AreEqual(1, starts);
                        Assert.IsTrue(processGateway.Session.Started);
                        StringAssert.Contains(processGateway.LaunchRequest.Arguments, path);
                    }
                    finally { processGateway.Session.KeepRunning = false; }
                });
            });
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>読込・再生・一時停止・再開・停止を実表へ反映し時刻通知だけでは再描画せず、行と成績・選択・未確定メモを保つことを確認し、再生状態変更で行交換や編集確定を行う誤りを検出します。</summary>
    /// <remarks>再生状態の判断はPlaybackPanelの下位ケース、Loadedの表とcompiled Binding・編集維持への配送は実WPFで分担します。</remarks>
    [TestMethod]
    public void PlaybackStateChangesRefreshLoadedTableWithoutReplacingRowsOrCommittingEditor()
    {
        string path = Path.GetTempFileName();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new PlaybackPanelViewModelTests.PreloadBmsPlayer
        {
            SupportsBmson = true,
            Ready = ready.Task,
            Completion = completion.Task
        };
        try
        {
            MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
                MainWindowViewModelTestFactory.CreateIsolatedSettings(),
                (scope, viewModel, window) =>
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.PlaybackPanel.ReplacePlayerAsync(player), "replace-player");
                    // playlistの所持行から再生するため、実pathと所持判定に使うhashを揃えます。
                    ChartFile chart = (ChartTestValues.Empty(ChartFileKind.Bmson) with { Path = path, Md5 = new string('a', 32), RawTitle = "Bmson" });
                    PlaylistDetailRow first = viewModel.MainChartList.RowProjection.CreatePlaylistDetailSourceRow(
                        null, new BMSTableEntry { memo = "original" }, chart,
                        new BMSScore { perfect = 100, great = 12, totalnotes = 200, IsLr2IrScoreUnsent = true }, null, null).CreateViewRow();
                    PlaylistDetailRow duplicate = viewModel.MainChartList.RowProjection.CreatePlaylistDetailSourceRow(
                        null, new BMSTableEntry(), chart, null, null, null).CreateViewRow();
                    var rows = new List<object> { first, duplicate };
                    viewModel.MainChartList.Rows = rows;
                    viewModel.MainChartList.SelectedIndex = -1;
                    var table = (CustomTableView)window.FindName("customTableView");
                    ChartFileStatus expected = ChartFileStatus.NONE;
                    var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    int evaluationCount = 0;
                    int refreshCount = 0;
                    EventHandler refresh = (_, _) => refreshCount++;
                    EventHandler<MainChartListCellEditBeginningEventArgs> allowEdit = (_, request) => request.Accepted = true;
                    viewModel.MainChartList.DisplayRefreshRequested += refresh;
                    viewModel.MainChartList.CellEditBeginningRequested += allowEdit;
                    var layout = new CustomTableColumnSettings.ColumnLayout { Width = 120, Visibility = Visibility.Visible };
                    table.Columns =
                    [
                        new CustomTableColumn("Memo", "Memo", layout, 0, null, TextAlignment.Left,
                            row => ((PlaylistDetailRow)row).memo, editPropertyName: "memo"),
                        new CustomTableColumn("State", "State", layout, 1, null, TextAlignment.Left, row =>
                        {
                            evaluationCount++;
                            ChartFileStatus status = ((PlaylistDetailRow)row).status & ChartFileStatus.PLAYALL;
                            if (ReferenceEquals(row, first) && status == expected) { rendered.TrySetResult(); } return status.ToString();
                        })
                    ];
                    Task? start = null;
                    try
                    {
                        // compiled内容をLoadedにし、shellのContentRenderedによる本番起動初期化は開始しません。
                        object content = window.Content;
                        window.Content = null;
                        var host = new Window { Content = content, DataContext = viewModel, Width = 1000, Height = 700 };
                        scope.ShowAndWaitForContentRendered(host);
                        TestUiDispatcherHost.AwaitPresentationOnDispatcher(rendered.Task, "initial-table-render");
                        table.HandleKeyDown(Key.Down, ModifierKeys.None);
                        Assert.IsTrue(table.HandleKeyDown(Key.F2, ModifierKeys.None));
                        TextBox editor = FindVisualChildren<TextBox>(table).Single();
                        editor.Text = "uncommitted";
                        int selectedIndex = table.SelectedIndex;
                        expected = ChartFileStatus.LOADING;
                        rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        start = viewModel.PlaybackPanel.StartAtIndex(0);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(
                            player.StartObserved.Task, start, "preparing-start"), "preparing-start");
                        TestUiDispatcherHost.AwaitPresentationOnDispatcher(rendered.Task, "loading-table-render");
                        expected = ChartFileStatus.PLAY;
                        rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        ready.TrySetResult();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(start, "ready-start");
                        TestUiDispatcherHost.AwaitPresentationOnDispatcher(rendered.Task, "playing-table-render");
                        foreach (ChartFileStatus status in new[] { ChartFileStatus.PAUSE, ChartFileStatus.PLAY })
                        {
                            expected = status;
                            rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            viewModel.PlaybackPanel.TogglePause();
                            TestUiDispatcherHost.AwaitPresentationOnDispatcher(rendered.Task, "pause-resume-table-render");
                            Assert.AreEqual(status, duplicate.status & ChartFileStatus.PLAYALL);
                        }
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        int evaluationsBeforeTime = evaluationCount;
                        int refreshesBeforeTime = refreshCount;
                        var timeApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        System.ComponentModel.PropertyChangedEventHandler timeChanged = (_, args) =>
                        {
                            if (args.PropertyName == nameof(PlaybackPanelViewModel.CurrentlyPlayingTime)) { timeApplied.TrySetResult(); }
                        };
                        viewModel.PlaybackPanel.PropertyChanged += timeChanged;
                        try
                        {
                            player.CurrentTime = TimeSpan.FromSeconds(1);
                            player.Raise(nameof(IBMSPlayer.CurrentTime));
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(timeApplied.Task, "player-time-consumer-applied");
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                        }
                        finally { viewModel.PlaybackPanel.PropertyChanged -= timeChanged; }
                        Assert.AreEqual(evaluationsBeforeTime, evaluationCount);
                        Assert.AreEqual(refreshesBeforeTime, refreshCount);
                        expected = ChartFileStatus.NONE;
                        rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.PlaybackPanel.StopPlayback(), "stop-playback");
                        TestUiDispatcherHost.AwaitPresentationOnDispatcher(rendered.Task, "stopped-table-render");
                        Assert.AreSame(rows, viewModel.MainChartList.Rows);
                        Assert.AreSame(rows, table.ItemsSource);
                        Assert.AreEqual(212, first.score);
                        Assert.AreEqual(ChartFileStatus.SCORE_UNSENT, first.status);
                        Assert.AreSame(first, rows[0]);
                        Assert.AreSame(duplicate, rows[1]);
                        Assert.AreEqual(selectedIndex, table.SelectedIndex);
                        Assert.AreEqual("original", first.memo);
                        Assert.AreSame(editor, FindVisualChildren<TextBox>(table).Single());
                        Assert.AreEqual("uncommitted", editor.Text);
                    }
                    finally
                    {
                        ready.TrySetResult();
                        completion.TrySetResult();
                        if (start != null)
                        {
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(start, "start-cleanup");
                        }

                        table.HandleKeyDown(Key.Escape, ModifierKeys.None);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.PlaybackPanel.StopPlayback(), "stop-cleanup");
                        viewModel.MainChartList.DisplayRefreshRequested -= refresh;
                        viewModel.MainChartList.CellEditBeginningRequested -= allowEdit;
                    }
                });
        }
        finally
        {
            ready.TrySetResult();
            completion.TrySetResult();
            File.Delete(path);
        }
    }

    /// <summary>内蔵Readyから実Viewとシェルへ届いた開始通知で表への初回復帰を保ち、後発の検索入力・MEMO編集へ古いフォーカス要求を適用する誤りを検出します。</summary>
    /// <remarks>0は入力なし、1は検索、2は本番受付を通るMEMO編集です。実MainWindowのContent・非ゼロHWND・前面照合を維持し、OS前面値と要求先だけを観測します。物理OSフォーカス取得と実LostKeyboardFocusは保証しません。状態通知と行保持は既存ケース、古いowner/View通知は各寿命ケースへ分担します。</remarks>
    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void InternalReady_RestoresTableOnceAndPreservesLaterSearchOrMemoInput(int inputKind)
    {
        var attempts = new List<(IInputElement Target, bool AfterInput)>();
        bool inputApplied = false;
        RunPlaybackFocusScenario(
            handle => handle,
            target => attempts.Add((target, inputApplied)),
            (viewModel, window, table, panel, start) =>
            {
                int attemptsAtDelivery = -1;
                int editEnded = 0;
                int acceptedMemoEdits = 0;
                TextBox? editor = null;
                var search = (KeywordSearchEditor)window.FindName("KeywordSearchEditor");
                int selectionStart = -1;
                int selectionLength = -1;
                string[] candidateTexts = [];
                var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                DispatcherOperation? applyInput = null;
                DispatcherOperation? inputBoundary = null;
                EventHandler<CustomTableCellEditEndedEventArgs> editEndedHandler = (_, _) => editEnded++;
                EventHandler<MainChartListCellEditBeginningEventArgs> editBeginning = (_, request) =>
                {
                    if (request.Accepted && request.Context.PropertyName == nameof(PlaylistDetailRow.memo)) { acceptedMemoEdits++; }
                };
                RoutedEventHandler started = (_, _) =>
                {
                    // XAMLのMainWindow受信が戻った地点を観測し、その後に許可された明示入力を配送します。
                    attemptsAtDelivery = attempts.Count;
                    applyInput = window.Dispatcher.InvokeAsync(() =>
                    {
                        if (inputKind == 1)
                        {
                            TextBox input = search.InputTextBoxControl;
                            input.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, table, input)
                            {
                                RoutedEvent = Keyboard.GotKeyboardFocusEvent
                            });
                            input.Text = "tit";
                            input.Select(1, 1);
                            Assert.IsTrue(search.HandleInputKey(Key.Space, ModifierKeys.Control));
                            candidateTexts = search.Presentation.VisibleItems.Select(item => item.DisplayText).ToArray();
                            Assert.IsTrue(candidateTexts.Length > 0, "検索支援候補を表示した状態を使います。");
                            selectionStart = input.SelectionStart;
                            selectionLength = input.SelectionLength;
                            inputApplied = true;
                        }
                        else if (inputKind == 2)
                        {
                            Assert.IsTrue(table.IsRowSelected(0), $"MEMO対象行が選択されています。SelectedIndex={table.SelectedIndex}, Rows={table.ItemsSource.Count}。");
                            Assert.IsTrue(table.SurfaceWidth > 2d, $"MEMO編集領域を配置します。Width={table.SurfaceWidth}, Height={table.SurfaceHeight}。");
                            Assert.IsTrue(table.HandleKeyDown(Key.Right, ModifierKeys.None), "後発入力で表示中のMEMOセルへ移動します。");
                            CustomTableColumn column = table.GetCurrentCellColumn(0);
                            Assert.IsNotNull(column);
                            Assert.AreEqual(nameof(PlaylistDetailRow.memo), column.EditPropertyName);
                            Assert.IsTrue(table.HandleKeyDown(Key.F2, ModifierKeys.None), "本番の編集受付を通るMEMO行を使います。");
                            Assert.AreEqual(1, acceptedMemoEdits, "本番のCanBeginDetailEditとGridRowResolverを通って受理されます。");
                            editor = FindVisualChildren<TextBox>(table).Single();
                            editor.Text = "uncommitted";
                            editor.Select(2, 3);
                            selectionStart = editor.SelectionStart;
                            selectionLength = editor.SelectionLength;
                            inputApplied = true;
                        }
                    }, DispatcherPriority.DataBind);
                    // 受信後の明示入力と、それ以前に投稿されたInput処理を通す境界です。
                    inputBoundary = window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                    delivered.TrySetResult();
                };
                table.CellEditEnded += editEndedHandler;
                viewModel.MainChartList.CellEditBeginningRequested += editBeginning;
                panel.PlaybackStarted += started;
                try
                {
                    start();
                    TestUiDispatcherHost.AwaitPresentationOnDispatcher(delivered.Task, "internal-ready-view-delivery");
                    Assert.IsNotNull(applyInput);
                    Assert.IsNotNull(inputBoundary);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(applyInput.Task, "explicit-input-after-ready");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(inputBoundary.Task, "ready-input-dispatch-boundary");
                    Assert.AreEqual(0, attempts.Count(attempt => attempt.AfterInput),
                        $"後発入力後の古い要求を残しません。通知受信終端の試行={attemptsAtDelivery}、Input配送通過後の試行={attempts.Count}。");
                    Assert.AreEqual(1, attemptsAtDelivery, "開始時の初回復帰を通知受信内で完了します。");
                    Assert.AreEqual(1, attempts.Count, "初回復帰後に短命要求を残しません。");
                    Assert.AreSame(table, attempts[0].Target);
                    Assert.IsFalse(attempts[0].AfterInput, "後発入力へ古い復帰を適用しません。");
                    Assert.AreEqual(0, editEnded);
                    Assert.IsTrue(table.ItemsSource[0] is PlaylistDetailRow { memo: "original" });
                    Assert.AreEqual(0, table.SelectedIndex);
                    if (inputKind == 1)
                    {
                        Assert.AreEqual("tit", search.InputTextBoxControl.Text);
                        Assert.AreEqual(selectionStart, search.InputTextBoxControl.SelectionStart);
                        Assert.AreEqual(selectionLength, search.InputTextBoxControl.SelectionLength);
                        CollectionAssert.AreEqual(candidateTexts, search.Presentation.VisibleItems.Select(item => item.DisplayText).ToArray());
                    }
                    else if (inputKind == 2)
                    {
                        Assert.IsNotNull(editor);
                        Assert.AreSame(editor, FindVisualChildren<TextBox>(table).Single());
                        Assert.AreEqual("uncommitted", editor.Text);
                        Assert.AreEqual(selectionStart, editor.SelectionStart);
                        Assert.AreEqual(selectionLength, editor.SelectionLength);
                    }
                }
                finally
                {
                    panel.PlaybackStarted -= started;
                    table.CellEditEnded -= editEndedHandler;
                    viewModel.MainChartList.CellEditBeginningRequested -= editBeginning;
                }
            });
    }

    /// <summary>内蔵Ready通知の面復帰は維持し、実シェルHWNDと前面値が異なると表へのフォーカス要求を出さないことを確認します。</summary>
    /// <remarks>OS前面値だけを代替し、実Ready・View接続・XAML受信と製品のHWND比較を通します。物理的な前面移動は通常WPF検証の対象にしません。</remarks>
    [TestMethod]
    public void InternalReady_ForegroundMismatchRestoresSurfaceWithoutRequestingTableFocus()
    {
        var attempts = new List<IInputElement>();
        RunPlaybackFocusScenario(_ => new IntPtr(-1), attempts.Add,
            (viewModel, window, _, panel, start) =>
            {
                var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                RoutedEventHandler started = (_, _) => delivered.TrySetResult();
                panel.PlaybackStarted += started;
                try
                {
                    start();
                    TestUiDispatcherHost.AwaitPresentationOnDispatcher(delivered.Task, "foreground-mismatch-ready-view-delivery");
                    DispatcherOperation inputBoundary = window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(inputBoundary.Task, "foreground-mismatch-input-boundary");
                    Assert.AreEqual(0, attempts.Count);
                    Assert.AreEqual(PlayerPanelState.TITLE_LARGE, panel.EffectivePlayerPanelState);
                    Assert.AreEqual(PlayerPanelState.TITLE_LARGE, viewModel.PlaybackPanel.PlayerPanelState);
                    var host = (ExternalPlayerHwndHost)panel.FindName("externalPlayerHost");
                    Assert.AreEqual(Visibility.Collapsed, host.Visibility);
                    Assert.IsNotNull(BindingOperations.GetMultiBindingExpression(host, UIElement.VisibilityProperty));
                    Assert.AreEqual(Visibility.Visible, ((Grid)panel.FindName("gridPlayerTitle")).Visibility);
                    Assert.AreEqual("Ready対象", ((TextBlock)panel.FindName("gridBMSPlayerControlsTitle")).Text);
                }
                finally { panel.PlaybackStarted -= started; }
            });
    }

    /// <summary>実MainWindowを非アクティブ表示し、通常表入口から内蔵Readyまでの処理と全所有資源を同じ寿命で回収します。</summary>
    private static void RunPlaybackFocusScenario(
        Func<IntPtr, IntPtr> readForeground,
        Action<IInputElement> requestFocus,
        Action<MainWindowViewModel, MainWindow, CustomTableView, PlaybackPanelView, Action> test)
    {
        string path = Path.GetTempFileName();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new PlaybackPanelViewModelTests.PreloadBmsPlayer
        {
            SupportsBmson = true,
            Ready = ready.Task,
            Completion = completion.Task
        };
        try
        {
            TestUiDispatcherHost.RunWindowTest(scope =>
            {
                Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
                {
                    values.PlayerPanelState = PlayerPanelState.TITLE_LARGE;
                    values.UsePlayerBMIIDXView = false;
                    values.UsePlayeruBMplay = false;
                    values.UsePlayerLR2body = false;
                    // Ready後の入力保持を、ContextIdleでの無関係な起動時ナビゲーションから分離します。
                    values.StartupSelectInstallPending = false;
                });
                var lifetime = new MainWindowPresentationTestHarness.PresentationApplicationLifetime();
                MainWindowViewModel viewModel = new ApplicationComposition(
                    settingsEditSession: new NoOpSettingsEditSession(settings),
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: lifetime,
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog()).CreateMainWindowViewModelForTest();
                var ownership = new MainWindowTestLifetime(viewModel, lifetime.ShutdownRequested.Task);
                ownership.Run(() =>
                {
                    MainWindowPresentationTestHarness.PrepareConstructorOnlyShell(viewModel);
                    viewModel.StartupUpdateWorkflow.NotifyClosing();
                    viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(false);
                    IntPtr shellHandle = IntPtr.Zero;
                    MainWindow window = ownership.CreateWindow(() => new MainWindow(viewModel, settingsWindowCreated: null,
                        readPlaybackForegroundWindow: () => readForeground(shellHandle),
                        requestPlaybackKeyboardFocus: requestFocus));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.PlaybackPanel.ReplacePlayerAsync(player), "focus-replace-internal-player");
                    // 本番の所持playlist行を使い、未所持除外のケースとは入力条件を分けます。
                    ChartFile chart = ChartTestValues.Empty(ChartFileKind.Bmson) with { Path = path, Md5 = new string('a', 32), RawTitle = "Ready対象" };
                    var entry = new BMSTableEntry { parent = new BMSTable(), memo = "original" };
                    PlaylistDetailRow row = viewModel.MainChartList.RowProjection.CreatePlaylistDetailSourceRow(
                        null, entry, chart, null, null, null).CreateViewRow();
                    viewModel.MainChartList.Rows = new List<object> { row };
                    viewModel.MainChartList.SelectedIndex = -1;
                    var table = (CustomTableView)window.FindName("customTableView");
                    MainWindowViewHostTests.ShowMainWindow(scope, window);
                    // Loaded時の列設定適用後に代表の編集列と、その実表示領域を準備します。
                    var layout = new CustomTableColumnSettings.ColumnLayout { Width = 180, Visibility = Visibility.Visible };
                    table.Columns = [new CustomTableColumn("Memo", "MEMO", layout, 0, null, TextAlignment.Left,
                        candidate => ((PlaylistDetailRow)candidate).memo, editPropertyName: nameof(PlaylistDetailRow.memo))];
                    table.Width = 600;
                    table.Height = 200;
                    table.UpdateLayout();
                    shellHandle = new WindowInteropHelper(window).Handle;
                    Assert.AreNotEqual(IntPtr.Zero, shellHandle);
                    Assert.AreSame(window, Window.GetWindow(table));
                    Assert.IsTrue(table.IsLoaded);
                    Assert.IsTrue(table.HandleKeyDown(Key.Down, ModifierKeys.None));
                    var panel = (PlaybackPanelView)window.FindName("playbackPanelView");
                    Assert.IsTrue(panel.IsLoaded);
                    Assert.AreEqual(PlayerPanelState.TITLE_LARGE, panel.EffectivePlayerPanelState);
                    Task? request = null;
                    try
                    {
                        test(viewModel, window, table, panel, () =>
                        {
                            request = window.RequestPlaybackFromTable(0, row);
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(
                                player.StartObserved.Task, request, "focus-internal-start-observed"), "focus-internal-start-observed");
                            ready.TrySetResult();
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(request, "focus-internal-ready-request");
                        });
                    }
                    finally
                    {
                        ready.TrySetResult();
                        completion.TrySetResult();
                        if (request != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(request, "focus-request-cleanup"); }
                        table.HandleKeyDown(Key.Escape, ModifierKeys.None);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.PlaybackPanel.StopPlayback(), "focus-playback-cleanup");
                    }
                });
            });
        }
        finally
        {
            ready.TrySetResult();
            completion.TrySetResult();
            File.Delete(path);
        }
    }

    private static IReadOnlyList<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        var result = new List<T>();
        Visit(root, result, new HashSet<DependencyObject>());
        return result;
    }

    private static void Visit<T>(DependencyObject current, List<T> result, HashSet<DependencyObject> visited)
        where T : DependencyObject
    {
        if (current == null || !visited.Add(current))
        {
            return;
        }
        if (current is T match)
        {
            result.Add(match);
        }
        if (current is not Visual && current is not System.Windows.Media.Media3D.Visual3D)
        {
            return;
        }
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
        {
            Visit(VisualTreeHelper.GetChild(current, index), result, visited);
        }
    }

    private static void RaiseKey(CustomTableView table, Key key)
        => table.HandleKeyDown(key, ModifierKeys.None);
}
