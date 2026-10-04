using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MainWindowPlaybackWpfTests
{
    [TestMethod]
    public void PlaybackControls_BindPanelStateAndCapabilitiesThroughPlaybackOwner()
    {
        object? selectedRow = null;
        var activatedRows = new List<(int RowIndex, object Row)>();
        var playbackTerminal = new MainWindowPlaybackTerminal(
            row => selectedRow = row,
            (rowIndex, row) =>
            {
                activatedRows.Add((rowIndex, row));
                return false;
            });

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            new Settings(),
            (viewModel, window) =>
            {
                using HwndSource visualHost = CreateVisualHost(window, "MainWindowPlaybackWpfTests");
                var panel = (PlaybackPanelView)window.FindName("playbackPanelView");
                Assert.IsNotNull(panel);
                Assert.AreSame(viewModel.PlaybackPanel, panel.DataContext);

                IReadOnlyList<Button> seekButtons = FindVisualChildren<Button>(panel)
                    .Where(button => BindingOperations.GetBindingBase(button, UIElement.IsEnabledProperty) is Binding binding
                        && string.Equals(binding.Path?.Path, "CanSeek", StringComparison.Ordinal))
                    .ToArray();
                Assert.AreEqual(2, seekButtons.Count);
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
                (viewModel, window) => TestUiDispatcherHost.RunWindowTest(scope =>
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.PlaybackPanel.ReplacePlayerAsync(player), "replace-player");
                    ChartFile chart = ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song { path = path, title = "Bmson" });
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
                            if (ReferenceEquals(row, first) && status == expected) rendered.TrySetResult();
                            return status.ToString();
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
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(rendered.Task, "initial-table-render");
                        table.HandleKeyDown(Key.Down, ModifierKeys.None);
                        Assert.IsTrue(table.HandleKeyDown(Key.F2, ModifierKeys.None));
                        TextBox editor = FindVisualChildren<TextBox>(table).Single();
                        editor.Text = "uncommitted";
                        int selectedIndex = table.SelectedIndex;
                        expected = ChartFileStatus.LOADING;
                        rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        start = viewModel.PlaybackPanel.StartAtIndex(0);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(player.StartObserved.Task, "preparing-start");
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(rendered.Task, "loading-table-render");
                        expected = ChartFileStatus.PLAY;
                        rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        ready.TrySetResult();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(start, "ready-start");
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(rendered.Task, "playing-table-render");
                        foreach (ChartFileStatus status in new[] { ChartFileStatus.PAUSE, ChartFileStatus.PLAY })
                        {
                            expected = status;
                            rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            viewModel.PlaybackPanel.TogglePause();
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(rendered.Task, "pause-resume-table-render");
                            Assert.AreEqual(status, duplicate.status & ChartFileStatus.PLAYALL);
                        }
                        TestUiDispatcherHost.Drain();
                        int evaluationsBeforeTime = evaluationCount;
                        int refreshesBeforeTime = refreshCount;
                        player.CurrentTime = TimeSpan.FromSeconds(1);
                        player.Raise(nameof(IBMSPlayer.CurrentTime));
                        TestUiDispatcherHost.Drain();
                        Assert.AreEqual(evaluationsBeforeTime, evaluationCount);
                        Assert.AreEqual(refreshesBeforeTime, refreshCount);
                        expected = ChartFileStatus.NONE;
                        rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.PlaybackPanel.StopPlayback(), "stop-playback");
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(rendered.Task, "stopped-table-render");
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
                        if (start != null) TestUiDispatcherHost.AwaitTaskOnDispatcher(start, "start-cleanup");
                        table.HandleKeyDown(Key.Escape, ModifierKeys.None);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.PlaybackPanel.StopPlayback(), "stop-cleanup");
                        viewModel.MainChartList.DisplayRefreshRequested -= refresh;
                        viewModel.MainChartList.CellEditBeginningRequested -= allowEdit;
                    }
                }));
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

    private static HwndSource CreateVisualHost(MainWindow window, string name)
    {
        var source = new HwndSource(new HwndSourceParameters(name)
        {
            Width = 1000,
            Height = 700,
            PositionX = 0,
            PositionY = 0
        });
        source.RootVisual = (Visual)window.Content;
        window.Measure(new Size(1000d, 700d));
        window.Arrange(new Rect(0d, 0d, 1000d, 700d));
        window.UpdateLayout();
        return source;
    }

    private static void RaiseKey(CustomTableView table, Key key)
        => table.HandleKeyDown(key, ModifierKeys.None);
}
