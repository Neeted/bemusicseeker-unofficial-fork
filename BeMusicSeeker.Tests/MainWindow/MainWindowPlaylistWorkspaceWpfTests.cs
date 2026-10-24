using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.BmsPlaylistTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>実プレイリスト画面の編集・通知・所有モーダル・終了と、compiled操作の配送を検証します。</summary>
/// <remarks>保存・候補・受付条件の網羅はPlaylistの管理主体へ分担し、ここでは実DB/HTTPからの表示反映とWindow所有、イベント接続を観測します。</remarks>
[TestClass]
// 実MainWindowと所有モーダルがApplication資源・共通SQLite/native音声終端へ到達するため並列化しません。
[DoNotParallelize]
public sealed class MainWindowPlaylistWorkspaceWpfTests
{
    private const string NativeModalFixturePlaylistName = "Native modal fixture";

    /// <summary>実MainWindowの単一配送から、開いたplaylistへ保守値・独立score・別表参照を局所配送します。score版とMD5の実batch・行通知・drain終端を待ち、実controlと集計TextBlockのBinding、無関係行・未訪問BMSONを確認し、条件網羅は各ownerへ分担します。</summary>
    [TestMethod]
    public void CatalogMaintenance_ActualMainWindowRefreshesOpenPlaylistAndBoundCells()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            Console.WriteLine("catalog_main_window fixture_begin");
            ActualMainWindowFixture fixture = CreateActualMainWindowFixture(scope, withScoreDatabase: true);
            Console.WriteLine("catalog_main_window fixture_ready");
            Task? opening = null;
            Task? mutation = null;
            try
            {
                string bmsPath = Path.Combine(fixture.Root, "bound.bms");
                File.WriteAllText(bmsPath, "#PLAYER 1\r\n#TITLE A owned\r\n#WAV01 missing.wav\r\n#00111:01\r\n");
                string bmsonPath = Path.Combine(fixture.Root, "unvisited.bmson");
                File.WriteAllText(bmsonPath,
                    "{\"version\":\"1.0.0\",\"info\":{\"title\":\"Z bmson\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\",\"init_bpm\":120,\"resolution\":240},\"sound_channels\":[{\"name\":\"missing.wav\",\"notes\":[{\"x\":1,\"y\":0}]}]}");
                ChartFile first = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsPath));
                ChartFile last = BmsonChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsonPath));
                var gateway = new BmsLibraryDbGateway(Path.Combine(fixture.Root, "data", "song.db"));
                gateway.UpsertSongs([first]);
                gateway.UpsertBmsonSongs([last]);
                using (var scoreDb = new SQLite.SQLiteConnection(Path.Combine(fixture.Root, "data", "score.db")))
                {
                    scoreDb.Insert(new LR2ScoreDB.score { hash = first.Md5, perfect = 1, totalnotes = 10 });
                }
                fixture.Library.InitializeScoresOnly(null);
                bool catalogReady = false;
                int deliveredCatalogRevision = -1;
                EventHandler<NormalLibraryRefreshAppliedEventArgs> catalogApplied = (_, args) =>
                {
                    deliveredCatalogRevision = args.NotificationBatch.CatalogRevision;
                    CatalogChartCollectionSnapshot? snapshot = args.NotificationBatch.CatalogCommits.LastOrDefault()?.Snapshot;
                    if (snapshot != null && snapshot.BmsRows.Count == 1 && snapshot.BmsonRows.Count == 1) { catalogReady = true; }
                };
                fixture.ViewModel.RegularChartList.NormalLibraryRefreshApplied += catalogApplied;
                try
                {
                    fixture.Library.BmsCharts = [first];
                    fixture.Library.BmsonCharts = [last];
                    CatalogChartCollectionSnapshot ready = fixture.Library.CaptureNormalLibraryChartSnapshot();
                    Assert.AreEqual(1, ready.BmsRows.Count);
                    Assert.AreEqual(1, ready.BmsonRows.Count);
                    // 空のDispatcher操作ではなく、同じ確定batchのfanoutとscheduler Completionまで待ちます。
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        fixture.ViewModel.RegularChartList.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None),
                        "catalog-main-window-fixture-delivery");
                    Console.WriteLine("catalog_main_window initial_revision={0} delivered_revision={1}", ready.CatalogRevision, deliveredCatalogRevision);
                    Assert.IsTrue(catalogReady, "初回の確定snapshotが実MainWindowの通常通知接続を通過します。");
                }
                finally { fixture.ViewModel.RegularChartList.NormalLibraryRefreshApplied -= catalogApplied; }
                fixture.Table.is_external_sync = false;
                fixture.Table.entries = new[] { new BMSTableEntry { playlist_id = 1, md5 = first.Md5, title = "A owned" } }
                    .Concat(Enumerable.Range(1, 64).Select(index => new BMSTableEntry
                    { playlist_id = 1, md5 = index.ToString("x32"), title = "M background " + index.ToString("D2") }))
                    .Append(new BMSTableEntry { playlist_id = 1, md5 = last.Md5, title = "Z bmson" }).ToList();
                fixture.Playlist.CommitBMSTableWithEntriesToDB(fixture.Table);
                var reference = new BMSTable { symbol = "REF", name = "Reference before", entries = [new BMSTableEntry(first)] };
                fixture.Library.SynchronizeReferenceBMSTables([reference]);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    fixture.ViewModel.RegularChartList.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None), "catalog-main-window-reference-initial");
                PlaylistWorkspaceViewModel workspace = fixture.ViewModel.PlaylistWorkspace;
                workspace.InitializePlaylistDetailFilter(ChartListFilterSnapshot.Default);
                workspace.InitializePlaylistDetailSort(new ChartListSortParameters
                { ColumnsName = nameof(PlaylistDetailRow.Title), Direction = System.ComponentModel.ListSortDirection.Ascending });
                workspace.RequestDetailSelection(fixture.Table);
                opening = workspace.RequestDetailRefreshTask(MainViewUpdateMode.PlaylistFilterSelected,
                    MainViewUpdateMode.PlaylistFilterSelected, MainViewUpdateMode.PlaylistFilterSelected,
                    useCoalescingWindow: false, openReadiness: default);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(opening, "catalog-main-window-playlist-open");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(workspace.WaitForDetailBuildIdleAsync(), "catalog-main-window-playlist-idle");
                Console.WriteLine("catalog_main_window playlist_ready");
                // 表示値を観測する列は、このfixtureで明示的に表示して既定の非表示設定へ依存しません。
                fixture.ViewModel.MainChartList.ColumnsSettings.WavHealth.Visibility = Visibility.Visible;
                fixture.ViewModel.MainChartList.ColumnsSettings.PlaylistSymbols.Visibility = Visibility.Visible;
                TestUiDispatcherHost.ProcessQueuedPresentation();
                var control = (CustomTableView)fixture.Window.FindName("customTableView");
                Assert.AreSame(fixture.ViewModel.MainChartList, control.DataContext);
                BindingExpression? rowsBinding = BindingOperations.GetBindingExpression(control, CustomTableView.ItemsSourceProperty);
                Assert.IsNotNull(rowsBinding, "compiled画面の実Bindingを使います。");
                rowsBinding.UpdateTarget();
                System.Collections.IList rows = fixture.ViewModel.MainChartList.Rows;
                Assert.AreSame(rows, control.ItemsSource);
                Assert.AreEqual(66, rows.Count);
                TextBlock summary = FindDescendants<TextBlock>(fixture.Window).Single(block =>
                    BindingOperations.GetBindingExpression(block, TextBlock.TextProperty)?.ParentBinding.Path?.Path == "MainChartList.SummaryText");
                BindingExpression summaryBinding = BindingOperations.GetBindingExpression(summary, TextBlock.TextProperty)
                    ?? throw new AssertFailedException("compiled集計TextBlockのBindingがありません。");
                summaryBinding.UpdateTarget();
                Assert.AreEqual("[66" + Resources.Num_songs + "]", summary.Text);
                PlaylistDetailRow visible = rows[0] as PlaylistDetailRow ?? throw new AssertFailedException("先頭の詳細行がありません。");
                Assert.AreEqual("A owned", visible.Title);
                Assert.IsNull(visible.WAVHealth);
                IChartListViewMetadata metadata = rows as IChartListViewMetadata ?? throw new AssertFailedException();
                int realized = metadata.RealizedRowCount;
                Assert.IsTrue(realized < rows.Count, "画面外の行を残した状態で配送を検証します。");
                bool updated = false;
                System.ComponentModel.PropertyChangedEventHandler changed = (_, args) =>
                {
                    // source交換は全property通知を使う。個別名と同じく、その通知時の実値を観測する。
                    if ((string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(PlaylistDetailRow.WAVHealth))
                        && visible.WAVHealth == 0)
                    { updated = true; }
                };
                visible.PropertyChanged += changed;
                try
                {
                    CatalogChartCollectionSnapshot current = fixture.Library.CaptureNormalLibraryChartSnapshot();
                    mutation = Task.Run(() => fixture.Library.RescanResourceHealthCharts([current.BmsRows[0], current.BmsonRows[0]]));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(mutation, "catalog-main-window-maintenance-producer");
                    Console.WriteLine("catalog_main_window maintenance_producer_complete revision={0}", fixture.Library.CatalogRevision);
                    // 保守配送の実終端より前に次のscore変更を合流させないよう、正式な配送Taskを回収します。
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        fixture.ViewModel.RegularChartList.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None),
                        "catalog-main-window-maintenance-delivery");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(workspace.WaitForDetailBuildIdleAsync(), "catalog-main-window-maintenance-consumer");
                    Assert.IsTrue(updated, "表示済みの行へWAVHealthのPropertyChangedが届きます。");
                    Assert.AreSame(rows, control.ItemsSource);
                    Assert.AreSame(visible, rows[0]);
                    Assert.AreEqual(realized, metadata.RealizedRowCount);
                    Assert.AreEqual("0%", control.Columns.Single(column => column.Id == "WavHealth").GetText(visible));
                    PlaylistDetailRow unvisited = rows[rows.Count - 1] as PlaylistDetailRow ?? throw new AssertFailedException("画面外の詳細行がありません。");
                    Assert.AreEqual("Z bmson", unvisited.Title);
                    Assert.AreEqual(0, unvisited.WAVHealth);
                    Assert.IsNull(first.WAVHealth);
                    Assert.IsNull(last.WAVHealth);

                    PlaylistDetailRow unrelated = rows[1] as PlaylistDetailRow ?? throw new AssertFailedException();
                    int scoreRealized = metadata.RealizedRowCount;
                    fixture.ViewModel.MainChartList.SelectedIndex = 1;
                    var scoreWork = new List<string>();
                    workspace.DetailWorkObserver = scoreWork.Add;
                    int scoreVersion = fixture.Library.ScoreSnapshotVersion;
                    var scoreBatchApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var scoreRowUpdated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    Task initialScoreDelivery = Task.CompletedTask;
                    Task targetScoreDelivery = Task.CompletedTask;
                    Task initialScoreObservation = Task.CompletedTask;
                    EventHandler<NormalLibraryRefreshAppliedEventArgs> scoreApplied = (_, args) =>
                    {
                        if (args.NotificationBatch.ScoreChanges.Any(change => change.Version > scoreVersion
                            && change.Md5Keys.Contains(first.Md5, StringComparer.OrdinalIgnoreCase)))
                        {
                            targetScoreDelivery = fixture.ViewModel.RegularChartList.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None);
                            scoreBatchApplied.TrySetResult();
                        }
                    };
                    System.ComponentModel.PropertyChangedEventHandler scoreChanged = (_, args) =>
                    {
                        if ((string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(PlaylistDetailRow.stddevVal))
                            && visible.stddevVal == 7) { scoreRowUpdated.TrySetResult(); }
                    };
                    fixture.ViewModel.RegularChartList.NormalLibraryRefreshApplied += scoreApplied;
                    visible.PropertyChanged += scoreChanged;
                    try
                    {
                        BMSScore score = fixture.Library.GetBMSScores().Single();
                        score.stddevVal = 7;
                        initialScoreDelivery = fixture.ViewModel.RegularChartList.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None);
                        // 旧drainの正常終端後に対象batchが予約される場合も待つ。失敗・取消だけは直ちに伝える。
                        initialScoreObservation = initialScoreDelivery.ContinueWith(completed =>
                        {
                            if (completed.Exception is AggregateException failure) { scoreBatchApplied.TrySetException(failure.InnerExceptions); }
                            else if (completed.IsCanceled) { scoreBatchApplied.TrySetCanceled(); }
                        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        TestUiDispatcherHost.AwaitPresentationOnDispatcher(scoreBatchApplied.Task, "catalog-main-window-score-batch");
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(initialScoreDelivery, targetScoreDelivery), "catalog-main-window-score-delivery");
                        TestUiDispatcherHost.AwaitPresentationOnDispatcher(scoreRowUpdated.Task, "catalog-main-window-score-row");
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(workspace.WaitForDetailBuildIdleAsync(), "catalog-main-window-score-consumer");
                        Assert.AreEqual(7d, visible.stddevVal);
                        Assert.AreSame(rows, fixture.ViewModel.MainChartList.Rows);
                        Assert.AreSame(rows, control.ItemsSource);
                        Assert.AreSame(visible, rows[0]);
                        Assert.AreSame(unrelated, rows[1]);
                        Assert.AreEqual(1, fixture.ViewModel.MainChartList.SelectedIndex);
                        Assert.AreEqual(scoreRealized, metadata.RealizedRowCount);
                        Assert.AreEqual(1, scoreWork.Count(kind => kind == "playlist_source_projection"));
                        Assert.AreEqual(0, scoreWork.Count(kind => kind == "playlist_dependency_index_entry"));
                        Assert.AreEqual(0, scoreWork.Count(kind => kind == "playlist_order_buffer"));
                    }
                    finally
                    {
                        fixture.ViewModel.RegularChartList.NormalLibraryRefreshApplied -= scoreApplied;
                        visible.PropertyChanged -= scoreChanged;
                        try
                        {
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(initialScoreDelivery, targetScoreDelivery, initialScoreObservation),
                                "catalog-main-window-score-cleanup");
                        }
                        finally { workspace.DetailWorkObserver = null; }
                    }

                    Assert.AreEqual("REF", visible.RefTablesSymbols);
                    fixture.Library.ApplyPlaylistReferenceEntryCommits([], [new(reference, "NEW", "Reference after", 0, 0)]);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        fixture.ViewModel.RegularChartList.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None), "catalog-main-window-reference-fanout");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(workspace.WaitForDetailBuildIdleAsync(), "catalog-main-window-reference-consumer");
                    Assert.AreSame(rows, control.ItemsSource);
                    Assert.AreSame(visible, rows[0]);
                    Assert.AreSame(unrelated, rows[1]);
                    Assert.AreEqual(scoreRealized, metadata.RealizedRowCount);
                    Assert.AreEqual("NEW", control.Columns.Single(column => column.Id == "PlaylistSymbols").GetText(visible));
                    Assert.AreEqual("Reference after", visible.RefTablesNames);
                    summaryBinding.UpdateTarget();
                    Assert.AreEqual("[66" + Resources.Num_songs + "]", summary.Text);
                }
                finally { visible.PropertyChanged -= changed; }
            }
            finally
            {
                try
                {
                    if (opening != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(opening, "catalog-main-window-open-cleanup"); }
                    if (mutation != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(mutation, "catalog-main-window-mutation-cleanup"); }
                }
                finally { fixture.Close(); }
            }
        });
    }

    /// <summary>実property画面のUp・Down・自然順を独立順と比較し、DB由来の自然順からの新編集、LR2の完全一致タイトルとfolder行まで接続します。保存前の正本と出力bytes、別表の出力・DBを保ちます。</summary>
    /// <remarks>実controlの選択とイベント接続、所有モーダル・保存Task・native閉鎖をこの代表で確認し、LR2全出力種別の条件はcustom-folder系列へ分担します。</remarks>
    [TestMethod]
    public void PlaylistPropertyFolderControls_SaveDraftOrderToDatabaseAndRealLr2Output()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            ActualMainWindowFixture fixture = CreateActualMainWindowFixture(scope, configureSettings: values =>
            {
                string root = values.BMSRootPath;
                values.OperationModeLR2DB = true;
                values.LR2RootPath = root;
                values.LR2SongDBPath = Path.Combine(root, "data", "song.db");
                values.LR2ConfigXmlPath = Path.Combine(root, "LR2files", "Config", "config.xml");
                Directory.CreateDirectory(Path.GetDirectoryName(values.LR2ConfigXmlPath) ?? throw new AssertFailedException());
                File.WriteAllText(values.LR2ConfigXmlPath, "<config><system /><jukebox /></config>");
                string songs = Path.Combine(root, "Songs");
                Directory.CreateDirectory(songs);
                values.BMSRootPath = songs;
                values.BMSInstallDir = songs;
                var config = new LR2Config(values.LR2ConfigXmlPath);
                config.AddBMSSearchDirectories([songs]);
                config.Save(config.ConfigFilePath);
                File.WriteAllBytes(Path.Combine(root, "LR2body.exe"), []);
                values.LR2CustomFolderOutputBaseDir = Path.Combine(root, "custom");
                values.LR2CustomFolderOutputBaseDirRootType = Path.Combine(root, "root-custom");
                values.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
                Directory.CreateDirectory(values.LR2CustomFolderOutputBaseDir);
                Directory.CreateDirectory(values.LR2CustomFolderOutputBaseDirRootType);
            });
            Task? operation = null;
            Exception? primaryFailure = null;
            try
            {
                string dbPath = Path.Combine(fixture.Root, "data", "song.db");
                BMSTable table = fixture.Table;
                table.name = "Order A";
                table.Output_dir = "OrderA";
                string symbol = table.symbol;
                table.Folder_order = ["F10", "F2", "F1"];
                table.is_external_sync = false;
                table.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder;
                table.entries = new[] { "F10", "F2", "F1" }.Select((folder, index) => new BMSTableEntry
                {
                    playlist_id = table.playlist_id,
                    md5 = new string((char)('a' + index), 32),
                    title = "Song " + folder,
                    folder = folder
                }).ToList();
                var other = new BMSTable
                {
                    playlist_id = 2,
                    name = "Order B",
                    symbol = "B",
                    Output_dir = "OrderB",
                    Folder_order = ["F2"],
                    ignore_folder_output = table.ignore_folder_output,
                    entries = [new BMSTableEntry { playlist_id = 2, md5 = new string('d', 32), title = "Other song", folder = "F2" }]
                };
                fixture.Playlist.BMSTables.Add(other);
                fixture.Playlist.ReOutputCustomFolderAndCommitToDB(table);
                fixture.Playlist.ReOutputCustomFolderAndCommitToDB(other);
                string otherOutput = Path.Combine(fixture.Root, "custom", "OrderB", "0001.lr2folder");
                byte[] otherBytes = File.ReadAllBytes(otherOutput);
                var summary = (CustomTableView)fixture.Window.FindName("customTablePlaylistSummary");
                PlaylistSummaryRow row = CreatePlaylistSummaryRow(table);
                summary.ItemsSource = new List<PlaylistSummaryRow> { row };
                summary.SelectRowsByPredicate(_ => true);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                string[] saved = ["F10", "F2", "F1"];
                string[] savedEffective = saved;
                BMSTable? naturalRead = null;
                foreach ((string action, string selected, string[] expected) in new[]
                {
                    ("Up", "F1", new[] { "F10", "F1", "F2" }),
                    ("Down", "F10", new[] { "F1", "F10", "F2" }),
                    ("Natural", string.Empty, new[] { "F1", "F2", "F10" })
                })
                {
                    string outputRoot = Path.Combine(fixture.Root, "custom", "OrderA");
                    Dictionary<string, byte[]> savedOutputBytes = Directory.GetFiles(outputRoot, "*.lr2folder", SearchOption.AllDirectories)
                        .ToDictionary(path => path, File.ReadAllBytes);
                    for (int index = 0; index < savedEffective.Length; index++)
                    {
                        string path = Path.Combine(outputRoot, (index + 1).ToString("D4", CultureInfo.InvariantCulture) + ".lr2folder");
                        Assert.AreEqual("#TITLE " + savedEffective[index], File.ReadAllLines(path, Encoding.GetEncoding("shift_jis"))
                            .Single(line => line.StartsWith("#TITLE ", StringComparison.Ordinal)));
                    }
                    OpenPropertyDialog(fixture.Window, summary, row, "property-folder-order-" + action, (dialog, _) =>
                    {
                        var draft = (PlaylistPropertyDialogViewModel)dialog.DataContext;
                        var navigation = (FrameworkElement)dialog.FindName("propertyNavigation");
                        var content = (FrameworkElement)dialog.FindName("propertyContent");
                        ObservePropertyNavigation(navigation, content).ItemsByCategory[PropertyNavigationCategory.Folder].Provider.Select();
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        var list = (ListBox)dialog.FindName("foldersListBox");
                        if (action == "Natural")
                        {
                            ((CheckBox)dialog.FindName("checkBoxFolderNatueralSort")).IsChecked = true;
                        }
                        else
                        {
                            list.SelectedItems.Clear();
                            list.SelectedItems.Add(selected);
                            Button button = FindAutomationButton(dialog, "PlaylistPropertyFolderMove" + action);
                            Assert.IsTrue(button.IsEnabled);
                            RaiseButtonClick(button);
                        }
                        CollectionAssert.AreEqual(expected, draft.folder_order.ToArray());
                        CollectionAssert.AreEqual(saved, table.Folder_order.ToArray());
                        CollectionAssert.AreEqual(savedEffective, table.folder_list.ToArray());
                        using (LR2SongDBExtended before = new BmsLibraryDbGateway(dbPath).OpenSongDbReadOnly())
                        { CollectionAssert.AreEqual(saved, before.Table<BMSTable>().Single(item => item.playlist_id == table.playlist_id).Folder_order.ToArray()); }
                        CollectionAssert.AreEquivalent(savedOutputBytes.Keys.ToArray(), Directory.GetFiles(outputRoot, "*.lr2folder", SearchOption.AllDirectories));
                        foreach ((string path, byte[] bytes) in savedOutputBytes) { CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path)); }
                        CollectionAssert.AreEqual(otherBytes, File.ReadAllBytes(otherOutput));
                        RaiseButtonClick(FindAutomationButton(dialog, "PlaylistPropertyAccept"));
                        operation = dialog.WaitForOperationCompletionAsync();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "property-folder-save");
                    });
                    using LR2SongDBExtended verify = new BmsLibraryDbGateway(dbPath).OpenSongDbReadOnly();
                    BMSTable read = verify.Table<BMSTable>().Single(item => item.playlist_id == table.playlist_id);
                    Assert.AreEqual("Order A", read.name);
                    Assert.AreEqual(symbol, read.symbol);
                    Assert.AreEqual("OrderA", read.Output_dir);
                    Assert.AreEqual(table.ignore_folder_output, read.ignore_folder_output);
                    Assert.IsFalse(read.is_external_sync);
                    if (action == "Natural")
                    {
                        read.entries = verify.Table<BMSTableEntry>().Where(entry => entry.playlist_id == table.playlist_id).ToList();
                        CollectionAssert.AreEqual(expected, read.folder_list.ToArray());
                        naturalRead = read;
                    }
                    else
                    {
                        CollectionAssert.AreEqual(expected, read.Folder_order.ToArray());
                    }
                    for (int index = 0; index < expected.Length; index++)
                    {
                        string path = Path.Combine(fixture.Root, "custom", "OrderA", (index + 1).ToString("D4", CultureInfo.InvariantCulture) + ".lr2folder");
                        string titleLine = File.ReadAllLines(path, Encoding.GetEncoding("shift_jis"))
                            .Single(line => line.StartsWith("#TITLE ", StringComparison.Ordinal));
                        Assert.AreEqual("#TITLE " + expected[index], titleLine);
                        LR2SongDB.folder folder = verify.Table<LR2SongDB.folder>().Single(item => item.path == path);
                        Assert.AreEqual(expected[index], folder.title);
                        Assert.AreEqual("Order A", folder.category);
                    }
                    BMSTable otherRead = verify.Table<BMSTable>().Single(item => item.playlist_id == 2);
                    Assert.AreEqual("B", otherRead.symbol);
                    CollectionAssert.AreEqual(new[] { "F2" }, otherRead.Folder_order.ToArray());
                    CollectionAssert.AreEqual(otherBytes, File.ReadAllBytes(otherOutput));
                    Assert.IsNull(fixture.ViewModel.PlaylistWorkspace.ActivePropertyDialog);
                    saved = read.Folder_order.ToArray();
                    savedEffective = expected;
                }
                Assert.IsNotNull(naturalRead);
                Assert.AreNotSame(table, naturalRead);
                Assert.AreSame(table, fixture.Playlist.BMSTables[0]);
                fixture.Playlist.BMSTables[0] = naturalRead;
                PlaylistSummaryRow freshRow = CreatePlaylistSummaryRow(naturalRead);
                summary.ItemsSource = new List<PlaylistSummaryRow> { freshRow };
                summary.SelectRowsByPredicate(_ => true);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                OpenPropertyDialog(fixture.Window, summary, freshRow, "property-natural-order-reopen", (dialog, _) =>
                {
                    var navigation = (FrameworkElement)dialog.FindName("propertyNavigation");
                    var content = (FrameworkElement)dialog.FindName("propertyContent");
                    ObservePropertyNavigation(navigation, content).ItemsByCategory[PropertyNavigationCategory.Folder].Provider.Select();
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreEqual(true, ((CheckBox)dialog.FindName("checkBoxFolderNatueralSort")).IsChecked);
                    CollectionAssert.AreEqual(new[] { "F1", "F2", "F10" }, ((PlaylistPropertyDialogViewModel)dialog.DataContext).folder_order.ToArray());
                    Assert.IsFalse(FindAutomationButton(dialog, "PlaylistPropertyFolderMoveUp").IsEnabled);
                    Assert.IsFalse(FindAutomationButton(dialog, "PlaylistPropertyFolderMoveDown").IsEnabled);
                    RaiseButtonClick(FindAutomationButton(dialog, "PlaylistPropertyCancel"));
                });
                Assert.IsNull(fixture.ViewModel.PlaylistWorkspace.ActivePropertyDialog);
            }
            catch (Exception failure)
            {
                primaryFailure = failure;
                throw;
            }
            finally
            {
                try
                {
                    if (operation != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "property folder operation cleanup"); }
                }
                catch (Exception cleanupFailure) when (primaryFailure != null) { Console.WriteLine(cleanupFailure); }
                finally { fixture.Close(); }
            }
        });
    }

    /// <summary>外部再同期で置換された旧行の編集を実所有通知で拒否し、現行のlevel・URL・memoとDBを保ち、現行行の編集だけを保存します。</summary>
    [TestMethod]
    public void ResyncedDetailEdit_RejectsOldRowThroughOwnedNotificationAndSavesCurrentRow()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var displayed = new List<(Window Owner, UiMessageRequest Request)>();
            ActualMainWindowFixture fixture = CreateActualMainWindowFixture(windowTest,
                messagePresenter: (owner, request) =>
                {
                    displayed.Add((owner, request));
                    return new ThemedMessageBoxResponse(MessageBoxResult.OK, closedWithoutSelection: false);
                });
            var started = new List<Task>();
            Exception? primaryFailure = null;
            try
            {
                string header = Path.Combine(fixture.Root, "detail-header.json");
                string data = Path.Combine(fixture.Root, "detail-data.json");
                File.WriteAllText(header, "{\"name\":\"Detail reload\",\"symbol\":\"E\",\"data_url\":\"./detail-data.json\",\"level_order\":[1]}", new UTF8Encoding(true));
                File.WriteAllText(data, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"level\":\"1\",\"url\":\"https://before.example/song\"}]", new UTF8Encoding(true));
                Task<BMSTable> load = fixture.Playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(header));
                started.Add(load);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(load, "detail-current-fixture-load");
                BMSTable original = load.GetAwaiter().GetResult();
                original.playlist_id = fixture.Table.playlist_id;
                original.EnableExternalSync();
                fixture.Playlist.BMSTables = new ObservableCollection<BMSTable>([original]);
                fixture.Playlist.CommitBMSTableWithEntriesToDB(original);
                PlaylistWorkspaceViewModel workspace = fixture.ViewModel.PlaylistWorkspace;
                BMSTableEntry oldEntry = original.entries.Single();
                workspace.RequestDetailSelection(original);
                Task oldDetail = workspace.WaitForDetailBuildIdleAsync();
                started.Add(oldDetail);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(oldDetail, "detail-original-row-presentation");
                PlaylistDetailRow oldRow = fixture.ViewModel.MainChartList.Rows[0] as PlaylistDetailRow
                    ?? throw new AssertFailedException("再同期前の実詳細行がありません。");
                Assert.AreSame(oldEntry, oldRow.Entry);
                File.WriteAllText(data, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"level\":\"9\",\"url\":\"https://current.example/song\"}]", new UTF8Encoding(true));
                Task resync = workspace.ResyncPlaylistsAsync([original]);
                started.Add(resync);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(resync, "detail-resync-actual-terminal");
                BMSTable current = fixture.Playlist.BMSTables.Single();
                BMSTableEntry currentEntry = current.entries.Single(entry => !entry.is_removed);
                Assert.AreNotSame(original, current);
                Assert.AreEqual(original.playlist_id, current.playlist_id);
                Assert.AreEqual(oldEntry.md5, currentEntry.md5);
                Assert.AreEqual(9d, currentEntry.level);
                Assert.AreEqual("https://current.example/song", currentEntry.url);
                Assert.IsFalse(MainWindowViewModelTestFactory.GetComposition(fixture.ViewModel).PlaylistOperationAdmission.IsActive);
                displayed.Clear();
                string expectedMemo = currentEntry.memo;
                PlaylistWorkspaceMutationRejectedEventArgs? rejection = null;
                workspace.MutationRejected += (_, request) => rejection = request;
                var oldContext = new MainChartListCellEditContext(oldRow, nameof(PlaylistDetailRow.memo), ChartOperationSourceScope.PlaylistOwned, MainViewOperationSection.Playlist);
                Task rejected = workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(oldContext, "old edit C", commit: true));
                started.Add(rejected);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(rejected, "detail-old-row-rejection-terminal");
                Assert.IsNotNull(rejection);
                Assert.IsTrue(rejection.IsStale);
                Assert.IsFalse(rejection.IsBusy);
                Assert.AreEqual(1, displayed.Count);
                Assert.AreSame(fixture.Window, displayed.Single().Owner);
                Assert.IsFalse(string.IsNullOrWhiteSpace(displayed.Single().Request.MessageBoxText));
                Assert.AreEqual(MessageBoxImage.Hand, displayed.Single().Request.Icon);
                Assert.AreEqual(9d, currentEntry.level);
                Assert.AreEqual("https://current.example/song", currentEntry.url);
                Assert.AreEqual(expectedMemo, currentEntry.memo);
                using (LR2SongDBExtended read = new BmsLibraryDbGateway(Path.Combine(fixture.Root, "data", "song.db")).OpenSongDbReadOnly())
                {
                    BMSTableEntry persisted = read.Table<BMSTableEntry>().Single(entry => entry.playlist_id == current.playlist_id && !entry.is_removed);
                    Assert.AreEqual(9d, persisted.level);
                    Assert.AreEqual("https://current.example/song", persisted.url);
                    Assert.AreEqual(expectedMemo, persisted.memo);
                }
                // 確定receiptの配送先である実source/viewから編集行を取得します。
                // workspaceに所属しない手製投影へ特別同期する保証は設けません。
                workspace.RequestDetailSelection(current);
                Task currentDetail = workspace.WaitForDetailBuildIdleAsync();
                started.Add(currentDetail);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(currentDetail, "detail-current-row-presentation");
                PlaylistDetailRow currentRow = fixture.ViewModel.MainChartList.Rows[0] as PlaylistDetailRow
                    ?? throw new AssertFailedException("再同期後の実詳細行がありません。");
                Assert.AreSame(currentEntry, currentRow.Entry);
                var currentContext = new MainChartListCellEditContext(currentRow, nameof(PlaylistDetailRow.memo), ChartOperationSourceScope.PlaylistOwned, MainViewOperationSection.Playlist);
                workspace.BeginDetailEdit(currentContext);
                currentRow.memo = "fresh edit D";
                Assert.AreEqual(expectedMemo, currentEntry.memo, "入力draftは正規保存前にentryを変更しません。");
                Task fresh = workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(currentContext, "fresh edit D", commit: true));
                started.Add(fresh);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(fresh, "detail-current-row-save-terminal");
                Assert.AreEqual(1, displayed.Count);
                Assert.AreEqual("fresh edit D", currentEntry.memo);
                Assert.AreEqual("fresh edit D", currentRow.memo);
                Assert.IsFalse(MainWindowViewModelTestFactory.GetComposition(fixture.ViewModel).PlaylistOperationAdmission.IsActive);
                using (LR2SongDBExtended read = new BmsLibraryDbGateway(Path.Combine(fixture.Root, "data", "song.db")).OpenSongDbReadOnly())
                {
                    BMSTableEntry persisted = read.Table<BMSTableEntry>().Single(entry => entry.playlist_id == current.playlist_id && !entry.is_removed);
                    Assert.AreEqual(9d, persisted.level);
                    Assert.AreEqual("https://current.example/song", persisted.url);
                    Assert.AreEqual("fresh edit D", persisted.memo);
                }
            }
            catch (Exception failure)
            {
                primaryFailure = failure;
                throw;
            }
            finally
            {
                try
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(started), "detail-edit-all-started-terminal");
                }
                catch when (primaryFailure != null) { }
                finally { fixture.Close(); }
            }
        });
    }

    /// <summary>全再読込み・単独同期・property保存から推薦表の自動名をDBと実ツリーへ反映し、名前の変更だけでは通知せず数値skill差だけを所有Windowへ通知します。</summary>
    [DataTestMethod]
    [DataRow("All", false)] // 全体再読込み、skill数値が変化。
    [DataRow("All", true)] // 全体再読込み、名だけ変化しskillは同値。
    [DataRow("Single", false)] // 単独同期、skill数値が変化。
    [DataRow("Single", true)] // 単独同期、名だけ変化しskillは同値。
    [DataRow("Property", false)] // propertyの同期有効化保存、skill数値が変化。
    [DataRow("Property", true)] // propertyの同期有効化保存、名だけ変化しskillは同値。
    [TestCategory("Playlist")]
    public void RecommendationReload_ThreeEntriesPublishAutomaticNamesAndPresentOnlyNumericSkillChanges(
        string entry, bool sameSkill)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var displayed = new List<(Window Owner, UiMessageRequest Request)>();
            TaskCompletionSource<object?> skillDisplayed = NewCompletion();
            ActualMainWindowFixture fixture = CreateActualMainWindowFixture(windowTest,
                recommendationScoreReader: _ => Task.FromResult(ReadWalkureInput("standard")),
                configureSettings: settings => settings.ShowRecommUpdatedMsg = true,
                messagePresenter: (owner, request) =>
                {
                    displayed.Add((owner, request));
                    if (request.Caption == Resources.Recommend_SkillUpdatedTitle && request.Icon == MessageBoxImage.Information)
                    {
                        skillDisplayed.TrySetResult(null);
                    }
                    return new ThemedMessageBoxResponse(
                        request.Button == MessageBoxButton.YesNo ? MessageBoxResult.Yes : MessageBoxResult.OK,
                        closedWithoutSelection: false);
                });
            PlaylistWorkspaceViewModel workspace = fixture.ViewModel.PlaylistWorkspace;
            TaskCompletionSource<PlaylistExternalSyncCompletionEventArgs> deferredCompletion = NewCompletion<PlaylistExternalSyncCompletionEventArgs>();
            EventHandler<PlaylistExternalSyncCompletionEventArgs> syncCompleted = (_, request) =>
            {
                if (request.FromReloadTables) { deferredCompletion.TrySetResult(request); }
            };
            workspace.PlaylistExternalSyncCompleted += syncCompleted;
            Task? operation = null;
            PlaylistPropertyDialogViewModel? property = null;
            Exception? primaryFailure = null;
            try
            {
                Task<BMSTable> initialLoad = fixture.Playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri("bmseeker:table.recommended"));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(initialLoad, "recommendation-fixture-load");
                BMSTable original = initialLoad.GetAwaiter().GetResult();
                original.playlist_id = fixture.Table.playlist_id;
                original.name = "Manual name ★999";
                original.org_name = sameSkill ? "旧方針 ★6.6" : "旧方針 ★3.87";
                original.header_sha256 = null;
                original.data_sha256 = null;
                original.last_update = new DateTime(2024, 6, 1, 10, 20, 30);
                original.is_bmt_output = false;
                if (entry == "Property") { original.DisableExternalSync(); } else { original.EnableExternalSync(); }
                fixture.Playlist.BMSTables = new ObservableCollection<BMSTable>([original]);
                fixture.Playlist.CommitBMSTableWithEntriesToDB(original);
                displayed.Clear();
                operation = ApplyEntryAsync();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "recommendation-entry-terminal");

                double skill = ReadWalkureCase("standard")["rating"]!.Value<double>("playerStarRating");
                string expectedName = string.Format(Resources.RecommendFormat, Resources.Recommended_standard,
                    skill.ToString("F2", CultureInfo.InvariantCulture));
                BMSTable active = fixture.Playlist.BMSTables.Single();
                Assert.AreEqual(expectedName, active.name);
                Assert.AreEqual(expectedName, active.org_name);
                Assert.AreEqual(original.last_update, active.last_update);
                var repository = new PlaylistPersistenceRepository(Path.Combine(fixture.Root, "data", "song.db"));
                BMSTable persisted = repository.LoadPlaylistHeaders().Single();
                Assert.AreEqual(expectedName, persisted.name);
                Assert.AreEqual(expectedName, persisted.org_name);
                Assert.AreEqual(original.last_update, persisted.last_update);
                Assert.IsFalse(string.IsNullOrWhiteSpace(persisted.header_sha256));
                Assert.IsFalse(string.IsNullOrWhiteSpace(persisted.data_sha256));

                // 実際に接続されたツリーのコンテナーと表示名を観測する。
                var playlistRoot = (TreeViewItem)fixture.Window.FindName("treeViewItemPlaylist");
                MaterializeTreeItems(playlistRoot);
                var tableItem = playlistRoot.ItemContainerGenerator.ContainerFromItem(active) as TreeViewItem;
                Assert.IsNotNull(tableItem);
                Assert.IsTrue(FindDescendants<TextBlock>(tableItem!).Any(block => block.Text == expectedName));
                (Window Owner, UiMessageRequest Request)[] skillMessages = displayed.Where(call => call.Request.Caption == Resources.Recommend_SkillUpdatedTitle
                    && call.Request.Icon == MessageBoxImage.Information).ToArray();
                Assert.AreEqual(sameSkill ? 0 : 1, skillMessages.Length);
                if (!sameSkill)
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(skillDisplayed.Task, "recommendation-message-presenter");
                    Assert.AreSame(fixture.Window, skillMessages[0].Owner);
                    Assert.AreEqual(string.Format(Resources.Recommend_SkillUpdatedMessage, skill.ToString("F2"),
                        (skill - 3.87).ToString(" (+#0.00); (-#0.00);")), skillMessages[0].Request.MessageBoxText);
                }

                async Task ApplyEntryAsync()
                {
                    switch (entry)
                    {
                        case "All":
                            await fixture.ViewModel.ReloadTablesAsync();
                            // 全体入口の完了後にも動く遅延同期の終端まで待つ。
                            PlaylistExternalSyncCompletionEventArgs completed = await deferredCompletion.Task;
                            Assert.IsTrue(completed.Succeeded);
                            Assert.IsFalse(completed.WasSkipped);
                            break;
                        case "Single":
                            await workspace.ResyncPlaylistTableAsync(original);
                            break;
                        case "Property":
                            property = await workspace.OpenPropertyDialogAsync(original);
                            Assert.IsNotNull(property);
                            property!.is_external_sync = true;
                            Assert.AreEqual(PlaylistPropertyDialogOperationResult.Completed, await property.SaveAndApplyAsync());
                            break;
                        default: throw new AssertFailedException("Unknown entry.");
                    }
                }
            }
            catch (Exception exception) { primaryFailure = exception; throw; }
            finally
            {
                try
                {
                    if (operation != null)
                    {
                        try { TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "recommendation-entry-drain"); }
                        catch when (primaryFailure != null) { }
                    }
                    if (property != null)
                    {
                        workspace.ClosePropertyDialog(property);
                        property.Dispose();
                    }
                    workspace.PlaylistExternalSyncCompleted -= syncCompleted;
                    fixture.Close();
                }
                catch when (primaryFailure != null) { }
            }
        });
    }

    /// <summary>
    /// 同じ所有プロパティ画面の実Bindingと保存ボタンから、外部置換後の実BMT失敗、
    /// 実Warningの本文・ownerと採用後のHand通知、両表示失敗後も正常終了する待機Task、画面維持、旧失敗を含まない再保存成功とCloseを確認します。出力失敗点の組合せは保存サービス群へ分担します。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)] // 実BMT Warningと最終Hand通知が正常に終了。
    [DataRow(true)] // 実BMT Warningと最終Handの表示も失敗するが、保存済み状態と次の明示操作を妨げない。
    public void PlaylistPropertyDialog_ExternalReplacementBmtFailureKeepsOwnedDialogAndResavesSameId(bool failNotification)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            bool enableOutput = false;
            string output = string.Empty;
            PlaylistPropertyDialog? shown = null;
            int failureNotices = 0;
            Exception? notificationObservationFailure = null;
            var notificationFailure = new IOException("property failure notification failed");
            var warningPresentationFailure = new IOException("property BMT warning presentation failed");
            var outputWarnings = new List<(Window Owner, UiMessageRequest Request)>();
            var bmtFailures = new List<BmtTableExportService.FileOperationFailure>();
            ActualMainWindowFixture? fixture = null;
            FileStream? blocker = null;
            try
            {
                fixture = CreateActualMainWindowFixture(windowTest,
                    messagePresenter: (owner, request) =>
                    {
                        if (request.Icon == MessageBoxImage.Exclamation && request is not UiConfirmationRequest)
                        {
                            outputWarnings.Add((owner, request));
                            // BMT ownerが独立して診断する表示故障であり、Workspaceのreload診断へ合算しません。
                            if (failNotification) { throw warningPresentationFailure; }
                        }
                        if (request.Icon == MessageBoxImage.Hand)
                        {
                            failureNotices++;
                            try
                            {
                                PlaylistPropertyDialog visible = shown ?? throw new AssertFailedException("Failure notification preceded dialog ownership.");
                                ActualMainWindowFixture active = fixture ?? throw new AssertFailedException("Failure notification preceded fixture initialization.");
                                Assert.AreSame(visible, owner);
                                Assert.AreSame(visible, request.Owner);
                                Assert.IsTrue(visible.IsVisible);
                                Assert.AreEqual(1, active.Playlist.BMSTables.Count);
                                Assert.AreNotSame(active.Table, active.Playlist.BMSTables.Single());
                                var draft = (PlaylistPropertyDialogViewModel)visible.DataContext;
                                CollectionAssert.AreEqual(active.Playlist.BMSTables.Single().folder_list.ToArray(), draft.folder_order.ToArray());
                                Assert.AreEqual(active.Playlist.BMSTables.Single().last_update, draft.last_update,
                                    "失敗通知より先にダイアログも確定参照を採用します。");
                            }
                            catch (Exception observationFailure)
                            {
                                // 本番の任意通知境界が表明失敗を捕捉しても、操作Taskの終端後に必ず検出する。
                                notificationObservationFailure = observationFailure;
                            }
                            if (failNotification) { throw notificationFailure; }
                        }
                        return new ThemedMessageBoxResponse(MessageBoxResult.OK, closedWithoutSelection: false);
                    },
                    bmtOptionsProvider: () => enableOutput
                        ? new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = true, BeatorajaBmtTablePath = output }
                        : new BeatorajaBmtOptionsSnapshot());
                Action<IReadOnlyList<BmtTableExportService.FileOperationFailure>> reportBmtFailures = fixture.Playlist.BmtOutput.FailureReporter;
                Assert.IsNotNull(reportBmtFailures, "標準compositionの実BMT Warning接続を保持します。");
                fixture.Playlist.BmtOutput.FailureReporter = failures =>
                {
                    bmtFailures.AddRange(failures);
                    reportBmtFailures(failures);
                };
                output = Path.Combine(fixture.Root, "property-bmt");
                Directory.CreateDirectory(output);
                string manifest = Path.Combine(output, BmtTableExportService.ManifestFileName);
                File.WriteAllText(manifest, "{\"files\":[],\"playlists\":{}}");
                blocker = new FileStream(manifest, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                string header = Path.Combine(fixture.Root, "external-header.json");
                string data = Path.Combine(fixture.Root, "external-data.json");
                File.WriteAllText(header, "{\"name\":\"External\",\"symbol\":\"E\",\"data_url\":\"external-data.json\"}");
                File.WriteAllText(data, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"External song\",\"level\":\"1\"}]");
                fixture.Table.Page_url = new Uri(Path.Combine(fixture.Root, "previous-header.json"));
                fixture.Table.Header_url = fixture.Table.Page_url;
                fixture.Table.Data_url = new Uri(data);
                fixture.Table.Output_dir = "PropertyExternal";
                fixture.Table.is_bmt_output = true;
                Assert.IsTrue(fixture.Table.EnableExternalSync());
                fixture.Playlist.CommitBMSTableWithEntriesToDB(fixture.Table);
                enableOutput = true;
                string songPath = ApplicationPathSnapshot.FromExecutablePath(Path.Combine(fixture.Root, "BeMusicSeeker.exe")).StandaloneSongDbPath;
                var summary = (CustomTableView)fixture.Window.FindName("customTablePlaylistSummary");
                PlaylistSummaryRow row = CreatePlaylistSummaryRow(fixture.Table);
                summary.ItemsSource = new List<PlaylistSummaryRow> { row };
                summary.SelectRowsByPredicate(_ => true);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                ActualMainWindowFixture activeFixture = fixture;
                ModalObservation<PlaylistPropertyDialog> observation = OpenPropertyDialog(fixture.Window, summary, row,
                    "property-external-replacement-bmt-failure", (dialog, _) =>
                    {
                        shown = dialog;
                        TextBox source = FindDescendants<TextBox>(dialog).Single(control =>
                            HasBindingPath(control, nameof(PlaylistPropertyDialogViewModel.Page_url)));
                        source.Text = new Uri(header).AbsoluteUri;
                        source.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                        Assert.AreEqual(new Uri(header), ((PlaylistPropertyDialogViewModel)dialog.DataContext).Page_url);
                        RaiseButtonClick(FindAutomationButton(dialog, "PlaylistPropertyAccept"));
                        Task firstSave = dialog.WaitForOperationCompletionAsync();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(firstSave, "property-first-failed-save");
                        Assert.IsTrue(firstSave.IsCompletedSuccessfully);
                        Assert.IsNull(notificationObservationFailure,
                            "通知時の所有者・可視状態・確定参照採用の観測が失敗しました。" + Environment.NewLine + notificationObservationFailure);
                        Task ownerWaitAfterCompletion = dialog.WaitForOperationCompletionAsync();
                        Assert.AreSame(firstSave, ownerWaitAfterCompletion);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(ownerWaitAfterCompletion, "property-owner-wait-after-notification");
                        Assert.IsTrue(dialog.IsEnabled);
                        Assert.AreEqual(1, failureNotices);
                        Assert.AreEqual(1, outputWarnings.Count, "実manifest失敗のWarningを一回表示しようとします。");
                        BmtTableExportService.FileOperationFailure manifestFailure = bmtFailures.Single();
                        Assert.AreEqual(output, manifestFailure.Path, "manifest公開の例外はBMT出力先の失敗として通知されます。");
                        StringAssert.Contains(manifestFailure.Cause, manifest);
                        (Window warningOwner, UiMessageRequest warningRequest) = outputWarnings.Single();
                        Assert.AreSame(dialog, warningOwner, "実coordinatorは開いているプロパティ画面をWarningのownerに解決します。");
                        Assert.AreSame(activeFixture.Window, warningRequest.Owner);
                        Assert.AreEqual(Resources.MessageBoxTitle_Warning, warningRequest.Caption);
                        Assert.AreEqual(string.Format(Resources.Beatoraja_bmt_output_failure_format, manifestFailure.Path, manifestFailure.Cause),
                            warningRequest.MessageBoxText);
                        Assert.IsFalse(warningRequest.MessageBoxText.Contains(warningPresentationFailure.Message, StringComparison.Ordinal));
                        Assert.IsTrue(dialog.IsVisible);
                        Assert.IsFalse(dialog.HasTerminalOutcome);
                        Assert.AreSame(dialog.DataContext, activeFixture.ViewModel.PlaylistWorkspace.ActivePropertyDialog);
                        BMSTable replacement = activeFixture.Playlist.BMSTables.Single();
                        Assert.AreNotSame(activeFixture.Table, replacement);
                        Assert.AreEqual(activeFixture.Table.playlist_id, replacement.playlist_id);
                        using (LR2SongDBExtended db = new BmsLibraryDbGateway(songPath).OpenSongDbReadOnly())
                        {
                            Assert.AreEqual(1, db.Table<BMSTable>().Count());
                            Assert.AreEqual(1, db.Table<LR2SongDBExtended.playlist_entry>().Count(entry => entry.playlist_id == replacement.playlist_id));
                        }
                        (blocker ?? throw new AssertFailedException("Output blocker was released before the failed save.")).Dispose();
                        blocker = null;
                        File.Delete(header);
                        File.Delete(data);
                        RaiseButtonClick(FindAutomationButton(dialog, "PlaylistPropertyAccept"));
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.WaitForOperationCompletionAsync(), "property-resave-closes");
                        Task ownerWaitAfterSuccess = dialog.WaitForOperationCompletionAsync();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(ownerWaitAfterSuccess, "property-owner-wait-after-success");
                        Assert.IsTrue(ownerWaitAfterSuccess.IsCompletedSuccessfully);
                        Assert.AreNotSame(firstSave, ownerWaitAfterSuccess);
                        Assert.IsTrue(dialog.HasTerminalOutcome);
                        Assert.AreEqual(1, failureNotices);
                        Assert.AreEqual(1, outputWarnings.Count, "次の保存へ旧Warningやその表示故障を持ち越しません。");
                        Assert.AreSame(replacement, activeFixture.Playlist.BMSTables.Single());
                    });
                Assert.AreSame(fixture.Window, observation.Owner);
                Assert.AreEqual(1, observation.DataContextDetachCount);
                Assert.IsNull(fixture.ViewModel.PlaylistWorkspace.ActivePropertyDialog);
                Assert.IsTrue(File.Exists(manifest));
                using LR2SongDBExtended verify = new BmsLibraryDbGateway(songPath).OpenSongDbReadOnly();
                Assert.AreEqual(1, verify.Table<BMSTable>().Count());
                Assert.AreEqual(1, verify.Table<LR2SongDBExtended.playlist_entry>().Count());
            }
            finally
            {
                try
                {
                    if (shown != null)
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(shown.WaitForOperationCompletionAsync(), "property-output-test-cleanup");
                    }
                }
                finally
                {
                    blocker?.Dispose();
                    fixture?.Close();
                }
            }
        });
    }

    /// <summary>実所有モーダルの再表示・一括適用を通し、Busy・DB失敗・出力失敗・通知失敗でdraftと確定済み変更を区別し、終端後に親有効化とDataContext解除を完了します。</summary>
    [TestMethod]
    [DataRow("success")] // 必要な出力が成功してdraftをreset。
    [DataRow("busy")] // 先行受付中なのでDBとdraftを変えない。
    [DataRow("database_failure")] // DB保存が失敗し、同じdraftで次の明示適用を通す。
    [DataRow("output_failure")] // DB確定後の出力が失敗して変更事実とdraftを保持。
    [DataRow("notification_failure")] // 出力失敗通知も失敗しても、確定済み変更とdraftを保持して実Taskを終える。
    [DataRow("confirmation_failure")] // 実確認中は適用Taskとclose guardを保持し、表示失敗では変更しない。
    public void MainWindowPlaylistDialogs_UseOwnedNativeModalLifetimeAndCleanup(string bulkOutcome)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            bool captureOutput = false;
            string outputDirectory = string.Empty;
            using var releaseOutput = new ManualResetEventSlim();
            var outputEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var displayed = new List<(Window Owner, UiMessageRequest Request)>();
            var notificationFailure = new IOException("bulk output failure notification failed");
            Task? confirmationApply = null;
            bool confirmationApplyWasPending = false;
            bool confirmationDialogWasDisabled = false;
            bool confirmationCloseWasGuarded = false;
            ActualMainWindowFixture fixture = CreateActualMainWindowFixture(windowTest,
                messagePresenter: (owner, request) =>
                {
                    displayed.Add((owner, request));
                    if (bulkOutcome == "confirmation_failure"
                        && request is UiConfirmationRequest
                        && request.Owner is PlaylistSummaryBulkEditDialog confirmationDialog)
                    {
                        confirmationApply = confirmationDialog.WaitForApplyCompletionAsync();
                        confirmationApplyWasPending = !confirmationApply.IsCompleted;
                        confirmationDialogWasDisabled = !confirmationDialog.IsEnabled;
                        confirmationDialog.Close();
                        confirmationCloseWasGuarded = confirmationDialog.IsVisible;
                        throw new IOException("bulk confirmation presentation failed");
                    }
                    if (bulkOutcome == "notification_failure" && request.Icon == MessageBoxImage.Hand && request.Owner is PlaylistSummaryBulkEditDialog) { throw notificationFailure; }
                    return new ThemedMessageBoxResponse(MessageBoxResult.OK, closedWithoutSelection: false);
                },
                bmtOptionsProvider: () =>
                {
                    if (!captureOutput) { return new BeatorajaBmtOptionsSnapshot(); }
                    outputEntered.TrySetResult();
                    releaseOutput.Wait();
                    return new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = true, BeatorajaBmtTablePath = outputDirectory };
                });
            IDisposable? busyLease = null;
            FileStream? outputBlocker = null;
            Task? apply = null;
            try
            {
                var summary = (CustomTableView)fixture.Window.FindName("customTablePlaylistSummary");
                PlaylistSummaryRow row = CreatePlaylistSummaryRow(fixture.Table);
                summary.ItemsSource = new List<PlaylistSummaryRow> { row };
                summary.SelectRowsByPredicate(_ => true);
                TestUiDispatcherHost.ProcessQueuedPresentation();

                ModalObservation<PlaylistPropertyDialog> first = OpenPropertyDialog(
                    fixture.Window,
                    summary,
                    row,
                    "MainWindowPlaylistWorkspaceWpfTests.property-open",
                    (dialog, _) =>
                    {
                        FrameworkElement navigation = (FrameworkElement)dialog.FindName("propertyNavigation")
                            ?? throw new AssertFailedException("Playlist property navigation was not materialized.");
                        FrameworkElement contentHost = (FrameworkElement)dialog.FindName("propertyContent")
                            ?? throw new AssertFailedException("Playlist property content host was not materialized.");
                        PropertyNavigationObservation navigationState = ObservePropertyNavigation(navigation, contentHost);
                        navigationState.ItemsByCategory[PropertyNavigationCategory.Folder].Provider.Select();
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        AssertSelectedPropertyCategory(navigationState, PropertyNavigationCategory.Folder);
                        Assert.AreEqual(PropertyNavigationCategory.Folder, IdentifyVisiblePropertyCategory(contentHost));
                        RaiseButtonClick(FindAutomationButton(dialog, "PlaylistPropertyCancel"));
                    });
                Assert.AreSame(fixture.Window, first.Owner);
                Assert.IsFalse(first.OwnerEnabled);
                Assert.IsInstanceOfType(first.DataContext, typeof(PlaylistPropertyDialogViewModel));
                Assert.AreEqual(1, first.DataContextDetachCount);
                Assert.IsNull(fixture.ViewModel.PlaylistWorkspace.ActivePropertyDialog);
                Assert.AreSame(fixture.Table, row.TableRef);
                fixture.ModalPreparation.AssertLatest(first.Window, expectedCount: 1);

                ModalObservation<PlaylistPropertyDialog> reopened = OpenPropertyDialog(
                    fixture.Window,
                    summary,
                    row,
                    "MainWindowPlaylistWorkspaceWpfTests.property-reopen",
                    (dialog, _) =>
                    {
                        FrameworkElement reopenedNavigation = (FrameworkElement)dialog.FindName("propertyNavigation")
                            ?? throw new AssertFailedException("Reopened playlist property navigation was not materialized.");
                        FrameworkElement reopenedContent = (FrameworkElement)dialog.FindName("propertyContent")
                            ?? throw new AssertFailedException("Reopened playlist property content host was not materialized.");
                        PropertyNavigationObservation reopenedNavigationState = ObservePropertyNavigation(reopenedNavigation, reopenedContent);
                        reopenedNavigationState.ItemsByCategory[PropertyNavigationCategory.Folder].Provider.Select();
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        AssertSelectedPropertyCategory(reopenedNavigationState, PropertyNavigationCategory.Folder);
                        Assert.AreEqual(PropertyNavigationCategory.Folder, IdentifyVisiblePropertyCategory(reopenedContent));
                        RaiseButtonClick(FindAutomationButton(dialog, "PlaylistPropertyCancel"));
                    });
                Assert.AreSame(fixture.Window, reopened.Owner);
                Assert.IsFalse(reopened.OwnerEnabled);
                Assert.AreNotSame(first.DataContext, reopened.DataContext);
                Assert.AreEqual(1, reopened.DataContextDetachCount);
                Assert.IsNull(fixture.ViewModel.PlaylistWorkspace.ActivePropertyDialog);
                fixture.ModalPreparation.AssertLatest(reopened.Window, expectedCount: 2);

                outputDirectory = Path.Combine(fixture.Root, "bulk-table");
                Directory.CreateDirectory(outputDirectory);
                string manifest = Path.Combine(outputDirectory, BmtTableExportService.ManifestFileName);
                File.WriteAllText(manifest, "{\"files\":[],\"playlists\":{}}");
                fixture.Table.Output_dir = "BulkNative";
                fixture.Table.entries = [new BMSTableEntry { playlist_id = fixture.Table.playlist_id, md5 = new string('a', 32), title = "A", folder = "1" }];
                fixture.Playlist.CommitBMSTableWithEntriesToDB(fixture.Table);
                if (bulkOutcome is "output_failure" or "notification_failure") { outputBlocker = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.None); }
                if (bulkOutcome == "database_failure")
                {
                    using var database = new LR2SongDBExtended(Path.Combine(fixture.Root, "data", "song.db"));
                    database.Execute("CREATE TRIGGER fail_bulk_save BEFORE INSERT ON playlist BEGIN SELECT RAISE(FAIL, 'forced bulk DB failure'); END;");
                }
                captureOutput = bulkOutcome is "success" or "output_failure" or "notification_failure";
                ModalObservation<PlaylistSummaryBulkEditDialog> bulk = OpenBulkDialog(
                    fixture.Window,
                    summary,
                    row,
                    "MainWindowPlaylistWorkspaceWpfTests.bulk-open",
                    (dialog, observation) =>
                    {
                        var draft = (PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel)dialog.DataContext;
                        if (bulkOutcome == "busy") { Assert.IsTrue(fixture.Playlist.TryEnterPlaylistMutation(out busyLease)); }
                        draft.BmtOutputOption = draft.OnOption;
                        if (bulkOutcome == "confirmation_failure") { draft.ExternalSyncOption = draft.OnOption; }
                        RaiseButtonClick(FindAutomationButton(dialog, bulkOutcome == "confirmation_failure"
                            ? "PlaylistSummaryApplyExternalSync"
                            : "PlaylistSummaryApplyBmtOutput"));
                        apply = confirmationApply ?? dialog.WaitForApplyCompletionAsync();
                        if (captureOutput)
                        {
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(
                                TestUiDispatcherHost.AwaitNotificationAsync(outputEntered.Task, apply, "bulk-required-output"), "bulk-output-arrival");
                            Assert.IsFalse(apply.IsCompleted);
                            Assert.IsFalse(dialog.IsEnabled);
                            Assert.AreSame(draft.OnOption, draft.BmtOutputOption, "実出力前にはdraftをresetしません。");
                            Assert.IsFalse(fixture.Playlist.TryEnterPlaylistMutation(out _));
                            Assert.AreEqual(0, displayed.Count);
                        }
                        releaseOutput.Set();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "bulk-compiled-apply-terminal");
                        if (bulkOutcome == "confirmation_failure")
                        {
                            Assert.IsNotNull(confirmationApply);
                            Assert.IsTrue(confirmationApplyWasPending);
                            Assert.IsTrue(confirmationDialogWasDisabled);
                            Assert.IsTrue(confirmationCloseWasGuarded);
                            Assert.IsFalse(fixture.Table.is_external_sync);
                            Assert.AreSame(draft.OnOption, draft.ExternalSyncOption);
                        }
                        Assert.IsTrue(dialog.IsEnabled);
                        Assert.AreSame(bulkOutcome == "success" ? draft.NoChangeOption : draft.OnOption, draft.BmtOutputOption);
                        bool saved = bulkOutcome is "success" or "output_failure" or "notification_failure";
                        Assert.AreEqual(saved, fixture.Table.is_bmt_output);
                        using (var database = new LR2SongDBExtended(Path.Combine(fixture.Root, "data", "song.db")))
                        {
                            Assert.AreEqual(saved ? 1L : 0L, database.ExecuteScalar<long>(
                                "SELECT COUNT(1) FROM playlist WHERE playlist_id = ? AND is_bmt_output = 1;", fixture.Table.playlist_id));
                            if (bulkOutcome == "database_failure")
                            {
                                database.Execute("DROP TRIGGER fail_bulk_save;");
                            }
                        }
                        if (bulkOutcome == "database_failure")
                        {
                            RaiseButtonClick(FindAutomationButton(dialog, "PlaylistSummaryApplyBmtOutput"));
                            apply = dialog.WaitForApplyCompletionAsync();
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "bulk-same-draft-after-db-failure");
                            Assert.AreSame(draft.NoChangeOption, draft.BmtOutputOption);
                            Assert.IsTrue(fixture.Table.is_bmt_output);
                            using var database = new LR2SongDBExtended(Path.Combine(fixture.Root, "data", "song.db"));
                            Assert.AreEqual(1L, database.ExecuteScalar<long>(
                                "SELECT COUNT(1) FROM playlist WHERE playlist_id = ? AND is_bmt_output = 1;", fixture.Table.playlist_id));
                        }
                        observation.VisibleAfterApply = dialog.IsVisible;
                        RaiseButtonClick(FindAutomationButton(dialog, "PlaylistSummaryClose"));
                    });
                Assert.AreSame(fixture.Window, bulk.Owner);
                Assert.IsFalse(bulk.OwnerEnabled);
                Assert.IsInstanceOfType(
                    bulk.DataContext,
                    typeof(PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel));
                Assert.IsTrue(bulk.VisibleAfterApply);
                Assert.AreEqual(bulkOutcome is not ("busy" or "confirmation_failure"), fixture.Table.is_bmt_output);
                var bulkFailures = displayed.Where(notification => notification.Request.Icon == MessageBoxImage.Hand).ToList();
                Assert.AreEqual(bulkOutcome == "success" ? 0 : 1, bulkFailures.Count);
                if (bulkFailures.Count != 0)
                {
                    Assert.AreSame(bulk.Window, bulkFailures.Single().Owner);
                    Assert.AreSame(bulk.Window, bulkFailures.Single().Request.Owner);
                    Assert.AreEqual(MessageBoxImage.Hand, bulkFailures.Single().Request.Icon);
                    StringAssert.Contains(bulkFailures.Single().Request.MessageBoxText, Resources.Msg_error_unexpected);
                }
                var confirmations = displayed.Where(notification => notification.Request is UiConfirmationRequest).ToList();
                Assert.AreEqual(bulkOutcome == "confirmation_failure" ? 1 : 0, confirmations.Count);
                if (confirmations.Count != 0) { Assert.AreSame(bulk.Window, confirmations.Single().Request.Owner); }
                var outputWarnings = displayed.Where(notification => notification.Request.Icon == MessageBoxImage.Exclamation
                    && notification.Request is not UiConfirmationRequest).ToList();
                Assert.AreEqual(bulkOutcome is "output_failure" or "notification_failure" ? 1 : 0, outputWarnings.Count);
                if (outputWarnings.Count != 0)
                {
                    Assert.AreSame(bulk.Window, outputWarnings.Single().Owner, "実coordinatorは現在のmodalを通知ownerに解決します。");
                    Assert.AreSame(fixture.Window, outputWarnings.Single().Request.Owner);
                }
                Assert.AreEqual(1, bulk.DataContextDetachCount);
                Assert.IsNull(fixture.ViewModel.PlaylistWorkspace.ActiveSummaryBulkEditDialog);
                fixture.ModalPreparation.AssertLatest(bulk.Window, expectedCount: 3);
            }
            finally
            {
                releaseOutput.Set();
                if (apply != null)
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(apply, "bulk-compiled-finally");
                }
                busyLease?.Dispose();
                outputBlocker?.Dispose();
                fixture.Close();
            }
        });
    }

    /// <summary>編集中propertyへ一回のnative Closeを送ると正本へresetして取消結果で閉じ、親を再有効化して所有状態とDataContextを解放します。</summary>
    [TestMethod]
    public void PlaylistPropertyDialog_NativeCloseRequestClosesAfterSynchronousReset()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            ActualMainWindowFixture fixture = CreateActualMainWindowFixture(windowTest);
            try
            {
                var summary = (CustomTableView)fixture.Window.FindName("customTablePlaylistSummary");
                PlaylistSummaryRow row = CreatePlaylistSummaryRow(fixture.Table);
                summary.ItemsSource = new List<PlaylistSummaryRow> { row };
                summary.SelectRowsByPredicate(_ => true);
                TestUiDispatcherHost.ProcessQueuedPresentation();

                int nativeCloseRequestCount = 0;
                bool closedAfterSingleNativeCloseRequest = false;
                bool testOwnedRetryUsed = false;
                string? nameAtClosed = null;
                ModalObservation<PlaylistPropertyDialog> observation = OpenPropertyDialog(
                    fixture.Window,
                    summary,
                    row,
                    "MainWindowPlaylistWorkspaceWpfTests.property-native-close",
                    (dialog, _) =>
                    {
                        const string draftPlaylistName = "Native modal draft";
                        var draft =
                            (PlaylistPropertyDialogViewModel)dialog.DataContext;
                        draft.name = draftPlaylistName;
                        Assert.AreEqual(draftPlaylistName, draft.name);

                        TaskCompletionSource<object?> closed = NewCompletion();
                        EventHandler closedHandler = (_, _) =>
                        {
                            if (dialog.DataContext is PlaylistPropertyDialogViewModel closedDraft)
                            {
                                nameAtClosed = closedDraft.name;
                            }
                            closed.TrySetResult(null);
                        };
                        dialog.Closed += closedHandler;
                        try
                        {
                            nativeCloseRequestCount++;
                            dialog.Close();
                            try
                            {
                                TestUiDispatcherHost.AwaitPresentationOnDispatcher(
                                    closed.Task,
                                    "MainWindowPlaylistWorkspaceWpfTests.property-native-close.closed");
                                closedAfterSingleNativeCloseRequest = true;
                            }
                            catch (TimeoutException)
                            {
                                // 一回Closeで閉じない失敗を記録した後、nested modal解放だけのために二回目を送ります。
                                testOwnedRetryUsed = dialog.IsVisible;
                                if (dialog.IsVisible)
                                {
                                    dialog.Close();
                                }
                            }
                        }
                        finally
                        {
                            dialog.Closed -= closedHandler;
                        }
                    });

                Assert.AreEqual(
                    1,
                    nativeCloseRequestCount,
                    "The title-bar close scenario must issue exactly one native close request.");
                Assert.IsTrue(
                    closedAfterSingleNativeCloseRequest,
                    $"One native close request must close after reset completion. Test-owned retry used: {testOwnedRetryUsed}.");
                Assert.AreEqual(
                    NativeModalFixturePlaylistName,
                    nameAtClosed,
                    "Reset must restore the fixture's original playlist name before the native window raises Closed.");
                Assert.IsTrue(
                    observation.Window.DialogResult.HasValue && !observation.Window.DialogResult.Value,
                    "A reset-backed native close must return the modal coordinator's cancelled/false result.");
                Assert.IsTrue(fixture.Window.IsEnabled, "The owner must be re-enabled after the modal route returns.");
                Assert.IsNull(fixture.ViewModel.PlaylistWorkspace.ActivePropertyDialog);
                Assert.IsNull(observation.Window.DataContext);
                Assert.AreEqual(1, observation.DataContextDetachCount);
            }
            finally
            {
                fixture.Close();
            }
        });
    }

    /// <summary>実一括適用のDB書込み待ち中に親Closeが来ても、所有dialogを先に閉じて適用の実終端を待ち、終了入口を一回実行して所有状態を解放します。</summary>
    [TestMethod]
    public void MainWindowClose_WaitsForBulkApplyBeforeTerminalShutdownAndCleansOwnedState()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            ActualMainWindowFixture fixture = CreateActualMainWindowFixture(windowTest);
            bool applyWriteBlockerHeld = false;
            Task? acceptedApplyCompletion = null;
            try
            {
                var summary = (CustomTableView)fixture.Window.FindName("customTablePlaylistSummary");
                PlaylistSummaryRow row = CreatePlaylistSummaryRow(fixture.Table);
                summary.ItemsSource = new List<PlaylistSummaryRow> { row };
                summary.SelectRowsByPredicate(_ => true);
                TestUiDispatcherHost.ProcessQueuedPresentation();

                void ReleaseApplyWriteBlocker()
                {
                    if (!applyWriteBlockerHeld)
                    {
                        return;
                    }
                    applyWriteBlockerHeld = false;
                    fixture.Playlist.FreeWriterLockBMSTables();
                }

                ModalObservation<PlaylistSummaryBulkEditDialog> shutdown = OpenBulkDialog(
                    fixture.Window,
                    summary,
                    row,
                    "MainWindowPlaylistWorkspaceWpfTests.bulk-shutdown",
                    (dialog, observation) =>
                    {
                        var draft =
                            (PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel)dialog.DataContext;
                        draft.BmtOutputOption = draft.OnOption;
                        fixture.Playlist.AcquireWriterLockBMSTables();
                        applyWriteBlockerHeld = true;
                        RaiseButtonClick(FindAutomationButton(dialog, "PlaylistSummaryApplyBmtOutput"));
                        Task applyCompletion = dialog.WaitForApplyCompletionAsync();
                        acceptedApplyCompletion = applyCompletion;
                        fixture.Lifetime.RequestShutdownAction = () =>
                            observation.ApplyCompletedAtTerminalRequest = applyCompletion.IsCompleted;
                        observation.ApplyWasPending = !dialog.IsEnabled;

                        void ReleaseApplyAfterOwnerShutdownClose(object? sender, EventArgs args)
                        {
                            dialog.Closed -= ReleaseApplyAfterOwnerShutdownClose;
                            Assert.IsTrue(
                                dialog.IsOwnerShutdownClose,
                                "Bulk dialog must close through the owner-shutdown path before the apply lock is released.");
                            observation.ShutdownCountBeforeLockRelease = fixture.Lifetime.RequestShutdownCount;
                            ReleaseApplyWriteBlocker();
                        }
                        dialog.Closed += ReleaseApplyAfterOwnerShutdownClose;
                        fixture.Window.Close();
                        observation.OwnerCloseWasCanceled = fixture.Window.IsVisible;
                        observation.ShutdownCountBeforeApplyCompletion = fixture.Lifetime.RequestShutdownCount;
                    });

                fixture.ModalPreparation.AssertLatest(shutdown.Window, expectedCount: 1);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(fixture.Lifetime.ShutdownRequested.Task, fixture.Window.CloseCompletion, "MainWindowPlaylistWorkspaceWpfTests.bulk-shutdown.terminal-request"), "MainWindowPlaylistWorkspaceWpfTests.bulk-shutdown.terminal-request");
                Assert.AreEqual(
                    1,
                    fixture.Lifetime.RequestShutdownCount,
                    $"shutdown={fixture.Lifetime.RequestShutdownCount}, applyPending={shutdown.ApplyWasPending}, ownerCloseCanceled={shutdown.OwnerCloseWasCanceled}, beforeApply={shutdown.ShutdownCountBeforeApplyCompletion}, beforeRelease={shutdown.ShutdownCountBeforeLockRelease}, visible={fixture.Window.IsVisible}");
                Assert.IsNull(fixture.ViewModel.PlaylistWorkspace.ActiveSummaryBulkEditDialog);
                Assert.IsNull(shutdown.Window.DataContext);
                Assert.AreEqual(1, shutdown.DataContextDetachCount);
                Assert.IsTrue(shutdown.ApplyWasPending);
                Assert.IsTrue(shutdown.OwnerCloseWasCanceled);
                Assert.AreEqual(0, shutdown.ShutdownCountBeforeApplyCompletion);
                Assert.AreEqual(0, shutdown.ShutdownCountBeforeLockRelease);
                Assert.IsTrue(shutdown.ApplyCompletedAtTerminalRequest);
                fixture.Window.Close();
                Assert.IsFalse(fixture.Window.IsVisible);

                // 終端許可後に所有側の終了信号が再入しても一回の最終終了を保ちます。
                fixture.ViewModel.ShellShutdownWorkflow.RequestTerminalApplicationShutdown();
                Assert.AreEqual(1, fixture.Lifetime.RequestShutdownCount);
            }
            finally
            {
                if (applyWriteBlockerHeld)
                {
                    applyWriteBlockerHeld = false;
                    fixture.Playlist.FreeWriterLockBMSTables();
                }
                try
                {
                    if (acceptedApplyCompletion != null)
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(acceptedApplyCompletion, "bulk-shutdown-finally-actual-apply");
                    }
                }
                finally { fixture.Close(); }
            }
        });
    }

    /// <summary>
    /// 実全体再読込みでBのHTTP本文を保持しても、保存済みAと取得失敗Cの今回サマリーを実画面へ公開し、
    /// 未完了Bの旧内容・日時と操作全体のP・Busy・進捗・通知終端を保ちます。
    /// </summary>
    /// <remarks>
    /// 実VM・DB再読込みからshell予約、summary build、compiled Bindingと描画セルまでの接続を担います。
    /// A/C完了進捗から二段のDispatcher予約を回収し、全体終端までの抑制への退行を旧表示の値で検出します。
    /// メニュー配送と失敗・取消・必須出力の条件網羅は既存の入口・管理主体テストへ分担します。
    /// </remarks>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ReloadTables_ActualMainWindowPublishesCompletedSummariesWhileAnotherHttpBodyIsPending()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            ActualMainWindowFixture fixture = CreateActualMainWindowFixture(windowTest, closeNextMessage: () => true);
            BlockingPlaylistJsonServer? server = null;
            Task? reload = null;
            Task? producer = null;
            Task? nextMutation = null;
            Exception? primaryFailure = null;
            try
            {
                const string oldData = "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"level\":\"1\"}]";
                string goodHeader = Path.Combine(fixture.Root, "good-header.json");
                string blockedHeader = Path.Combine(fixture.Root, "blocked-header.json");
                string badHeader = Path.Combine(fixture.Root, "bad-header.json");
                string goodData = Path.Combine(fixture.Root, "good-data.json");
                string blockedData = Path.Combine(fixture.Root, "blocked-data.json");
                File.WriteAllText(goodHeader, "{\"name\":\"A success\",\"symbol\":\"A\",\"data_url\":\"good-data.json\"}");
                File.WriteAllText(blockedHeader, "{\"name\":\"B blocked\",\"symbol\":\"B\",\"data_url\":\"blocked-data.json\"}");
                File.WriteAllText(badHeader, "{\"name\":\"C failed\",\"symbol\":\"C\",\"data_url\":\"blocked-data.json\"}");
                File.WriteAllText(goodData, oldData);
                File.WriteAllText(blockedData, oldData);
                Task<BMSTable> goodLoad = fixture.Playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(goodHeader));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(goodLoad, "summary-reload.seed-a");
                BMSTable good = goodLoad.Result;
                Task<BMSTable> blockedLoad = fixture.Playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(blockedHeader));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(blockedLoad, "summary-reload.seed-b");
                BMSTable blocked = blockedLoad.Result;
                Task<BMSTable> badLoad = fixture.Playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(badHeader));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(badLoad, "summary-reload.seed-c");
                BMSTable bad = badLoad.Result;
                server = new BlockingPlaylistJsonServer(
                    "{\"name\":\"B blocked\",\"symbol\":\"B\",\"data_url\":\"./score.json\"}",
                    "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"B after\",\"level\":\"1\"}]");
                blocked.Page_url = server.PageUri;
                blocked.Header_url = server.PageUri;
                blocked.Data_url = new Uri("./score.json", UriKind.Relative);
                good.playlist_id = 7531;
                blocked.playlist_id = 7532;
                bad.playlist_id = 7533;
                DateTime oldUpdate = new(2001, 2, 3, 4, 5, 6);
                foreach (BMSTable table in new[] { good, blocked, bad })
                {
                    Assert.IsFalse(string.IsNullOrWhiteSpace(table.header_sha256));
                    Assert.IsFalse(string.IsNullOrWhiteSpace(table.data_sha256));
                    table.last_update = oldUpdate;
                    table.is_bmt_output = false;
                    Assert.IsTrue(table.EnableExternalSync());
                }
                bad.entries.Single().memo = "keep-failed-local";
                fixture.Playlist.BMSTables = new ObservableCollection<BMSTable>([good, blocked, bad]);
                fixture.Playlist.CommitBMSTablesWithEntriesToDB(fixture.Playlist.BMSTables);
                PlaylistWorkspaceViewModel workspace = fixture.ViewModel.PlaylistWorkspace;
                workspace.RefreshPlaylistTreeTables(fixture.Playlist, fixture.Library);
                workspace.SetPlaylistSummaryMode(true);
                // 観測する二列だけを表示領域へ配置し、既定の全幅・列順には依存しない。
                foreach (PlaylistSummaryColumnSettings.ColumnLayout layout in
                    CustomTableColumnFactory.EnumeratePlaylistSummaryColumnLayouts(workspace.PlaylistSummaryColumnsSettings))
                { layout.Visibility = Visibility.Collapsed; }
                workspace.PlaylistSummaryColumnsSettings.LastUpdate.Visibility = Visibility.Visible;
                workspace.PlaylistSummaryColumnsSettings.LastUpdate.DisplayIndex = 0;
                workspace.PlaylistSummaryColumnsSettings.LastUpdate.Width = 170;
                workspace.PlaylistSummaryColumnsSettings.Status.Visibility = Visibility.Visible;
                workspace.PlaylistSummaryColumnsSettings.Status.DisplayIndex = 1;
                workspace.PlaylistSummaryColumnsSettings.Status.Width = 130;
                workspace.RebuildPlaylistSummaryView(runAsync: false);
                PumpSummary(workspace, fixture.Window);
                var summary = (CustomTableView)fixture.Window.FindName("customTablePlaylistSummary");
                Assert.IsNotNull(BindingOperations.GetBindingExpression(summary, CustomTableView.ItemsSourceProperty));
                Assert.AreSame(workspace.PlaylistSummaryView, summary.ItemsSource);
                foreach (BMSTable table in new[] { good, blocked, bad })
                {
                    PlaylistSummaryRow row = workspace.PlaylistSummaryView.Single(item => item.PlaylistId == table.playlist_id);
                    Assert.AreSame(table, row.TableRef);
                    Assert.AreEqual(oldUpdate, row.LastUpdate);
                    Assert.AreEqual(Resources.Playlist_sync_status_none, row.Status);
                    AssertSummaryCells(summary, row, oldUpdate, Resources.Playlist_sync_status_none);
                }

                TaskCompletionSource<PlaylistExternalSyncRequestEventArgs> queued = NewCompletion<PlaylistExternalSyncRequestEventArgs>();
                TaskCompletionSource<object?> completedTargets = NewCompletion();
                TaskCompletionSource<PlaylistExternalSyncCompletionEventArgs> completed = NewCompletion<PlaylistExternalSyncCompletionEventArgs>();
                TaskCompletionSource<object?> progressTerminal = NewCompletion();
                var progress = new ConcurrentQueue<PlaylistSyncProgressSnapshot>();
                int notificationTerminalCount = 0;
                BMSTable? blockedAtSyncStart = null;
                BMSTable? badAtSyncStart = null;
                workspace.PlaylistExternalSyncQueued += (_, request) =>
                {
                    if (request.Reason != "ReloadTables") { return; }
                    blockedAtSyncStart = fixture.Playlist.BMSTables.FirstOrDefault(table => table.playlist_id == blocked.playlist_id);
                    badAtSyncStart = fixture.Playlist.BMSTables.FirstOrDefault(table => table.playlist_id == bad.playlist_id);
                    queued.TrySetResult(request);
                };
                workspace.PlaylistSyncProgressChanged += (_, request) =>
                {
                    if (!queued.Task.IsCompletedSuccessfully
                        || request.Snapshot.Source != "external_playlist_sync"
                        || request.Snapshot.Request != queued.Task.Result.ProgressRequest
                        || request.Snapshot.Request?.OperationToken != queued.Task.Result.OperationToken)
                    { return; }
                    progress.Enqueue(request.Snapshot);
                    if (request.Snapshot.IsActive && request.Snapshot.CompletedTableCount == 2)
                    { completedTargets.TrySetResult(null); }
                    if (!request.Snapshot.IsActive) { progressTerminal.TrySetResult(null); }
                };
                workspace.PlaylistExternalSyncCompleted += (_, request) =>
                {
                    if (queued.Task.IsCompletedSuccessfully && request.Reason == "ReloadTables"
                        && request.OperationToken == queued.Task.Result.OperationToken
                        && request.Version == queued.Task.Result.Version)
                    { completed.TrySetResult(request); }
                };
                workspace.PlaylistOperationNotificationPresentationRequested += (_, request) =>
                {
                    if (request.RouteName == "external playlist sync notification")
                    { Interlocked.Increment(ref notificationTerminalCount); }
                };
                File.WriteAllText(goodData, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"A after\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Added\",\"level\":\"2\"}]");
                File.WriteAllText(badHeader, "{ invalid json }");
                reload = fixture.ViewModel.ReloadTablesAsync();
                producer = Task.WhenAny(reload, server.Completion).Unwrap();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    TestUiDispatcherHost.AwaitNotificationAsync(queued.Task, producer, "summary-reload.sync-start"), "summary-reload.sync-start");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    TestUiDispatcherHost.AwaitNotificationAsync(server.DataRequestAccepted.Task, producer, "summary-reload.http-body-gate"), "summary-reload.http-body-gate");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    TestUiDispatcherHost.AwaitNotificationAsync(completedTargets.Task, producer, "summary-reload.a-c-callbacks"), "summary-reload.a-c-callbacks");
                PumpSummary(workspace, fixture.Window);
                Assert.AreSame(workspace.PlaylistSummaryView, summary.ItemsSource);

                PlaylistExternalSyncRequestEventArgs request = queued.Task.Result;
                Assert.IsTrue(request.OperationToken > 0);
                Assert.IsTrue(request.Version > 0);
                Assert.IsNotNull(request.ProgressRequest);
                Assert.IsTrue(request.FromReloadTables);
                Assert.IsNotNull(blockedAtSyncStart);
                Assert.IsNotNull(badAtSyncStart);
                BMSTable confirmed = fixture.Playlist.BMSTables.Single(table => table.playlist_id == good.playlist_id);
                Assert.AreEqual("A after", confirmed.entries.First().title);
                Assert.AreNotEqual(oldUpdate, confirmed.last_update);
                PlaylistSummaryRow goodRow = workspace.PlaylistSummaryView.Single(row => row.PlaylistId == good.playlist_id);
                Assert.AreSame(confirmed, goodRow.TableRef);
                Assert.AreEqual(confirmed.last_update, goodRow.LastUpdate);
                Assert.AreEqual(Resources.Playlist_sync_status_updated, goodRow.Status);
                Assert.IsFalse(goodRow.HasFailureStatus);
                AssertSummaryCells(summary, goodRow, confirmed.last_update, Resources.Playlist_sync_status_updated);
                PlaylistSummaryRow badRow = workspace.PlaylistSummaryView.Single(row => row.PlaylistId == bad.playlist_id);
                Assert.AreSame(badAtSyncStart, badRow.TableRef);
                Assert.AreSame(badAtSyncStart, fixture.Playlist.BMSTables.Single(table => table.playlist_id == bad.playlist_id));
                Assert.AreEqual("Before", badAtSyncStart.entries.Single().title);
                Assert.AreEqual("keep-failed-local", badAtSyncStart.entries.Single().memo);
                Assert.AreEqual(oldUpdate, badRow.LastUpdate);
                Assert.AreEqual(Resources.Playlist_sync_status_header, badRow.Status);
                Assert.IsTrue(badRow.HasFailureStatus);
                AssertSummaryCells(summary, badRow, oldUpdate, Resources.Playlist_sync_status_header);
                PlaylistSummaryRow blockedRow = workspace.PlaylistSummaryView.Single(row => row.PlaylistId == blocked.playlist_id);
                Assert.AreSame(blockedAtSyncStart, blockedRow.TableRef);
                Assert.AreSame(blockedAtSyncStart, fixture.Playlist.BMSTables.Single(table => table.playlist_id == blocked.playlist_id));
                Assert.AreEqual("Before", blockedAtSyncStart.entries.Single().title);
                Assert.AreEqual(oldUpdate, blockedRow.LastUpdate);
                Assert.AreEqual(Resources.Playlist_sync_status_none, blockedRow.Status);
                AssertSummaryCells(summary, blockedRow, oldUpdate, Resources.Playlist_sync_status_none);
                using (LR2SongDBExtended database = new BmsLibraryDbGateway(Path.Combine(fixture.Root, "data", "song.db")).OpenSongDbReadOnly())
                {
                    Assert.AreEqual(confirmed.last_update, database.Table<BMSTable>().Single(table => table.playlist_id == good.playlist_id).last_update);
                    Assert.AreEqual(2, database.Table<LR2SongDBExtended.playlist_entry>().Count(entry => entry.playlist_id == good.playlist_id));
                    Assert.AreEqual(oldUpdate, database.Table<BMSTable>().Single(table => table.playlist_id == bad.playlist_id).last_update);
                    LR2SongDBExtended.playlist_entry persistedBad = database.Table<LR2SongDBExtended.playlist_entry>().Single(entry => entry.playlist_id == bad.playlist_id);
                    Assert.AreEqual("Before", persistedBad.title);
                    Assert.AreEqual("keep-failed-local", persistedBad.memo);
                    Assert.AreEqual(oldUpdate, database.Table<BMSTable>().Single(table => table.playlist_id == blocked.playlist_id).last_update);
                    Assert.AreEqual("Before", database.Table<LR2SongDBExtended.playlist_entry>().Single(entry => entry.playlist_id == blocked.playlist_id).title);
                }
                Assert.IsFalse(reload.IsCompleted);
                Assert.IsTrue(fixture.Playlist.IsPlaylistUpdating);
                bool entered = fixture.Playlist.TryEnterPlaylistMutation(out IDisposable admission);
                admission?.Dispose();
                Assert.IsFalse(entered, "部分表示だけでは全体Pを解放しません。");
                StartupProgressWorkflowOwner startup = fixture.ViewModel.ProgressHub.StartupProgress;
                Assert.IsTrue(startup.IsOperationActive);
                Assert.IsTrue(fixture.ViewModel.IsLibraryOperationInProgress);
                Assert.AreEqual(request.OperationToken, startup.GetActiveStartupProgressOperationToken());
                Assert.IsTrue(progress.Last().IsActive);
                Assert.IsTrue(progress.Any(snapshot => snapshot.IsActive && snapshot.CompletedTableCount == 2));
                Assert.IsFalse(completed.Task.IsCompleted);
                Assert.IsFalse(progressTerminal.Task.IsCompleted);
                Assert.AreEqual(0, Volatile.Read(ref notificationTerminalCount));
                Assert.IsTrue(fixture.ViewModel.ProgressHub.Rows.Any(row => row.ParentKey == "startup"
                    && (row.Key.StartsWith("background:external_playlist_sync:", StringComparison.Ordinal)
                        || row.Key.StartsWith("playlist:external_playlist_sync:", StringComparison.Ordinal))));

                server.ReleaseResponse();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(reload, "summary-reload.actual-terminal");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    TestUiDispatcherHost.AwaitNotificationAsync(completed.Task, reload, "summary-reload.completed"), "summary-reload.completed");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    TestUiDispatcherHost.AwaitNotificationAsync(progressTerminal.Task, reload, "summary-reload.progress-terminal"), "summary-reload.progress-terminal");
                PumpSummary(workspace, fixture.Window);
                Assert.IsFalse(completed.Task.Result.Succeeded, "通常取得失敗Cを全件成功へ読み替えません。");
                Assert.AreEqual(1, Volatile.Read(ref notificationTerminalCount));
                Assert.IsFalse(fixture.Playlist.IsPlaylistUpdating);
                Assert.IsFalse(fixture.ViewModel.IsLibraryOperationInProgress);
                Assert.IsFalse(progress.Last().IsActive);
                Assert.IsTrue(progress.Any(snapshot => snapshot.TotalTableCount == 3 && snapshot.CompletedTableCount == 3));
                BMSTable confirmedBlocked = fixture.Playlist.BMSTables.Single(table => table.playlist_id == blocked.playlist_id);
                Assert.AreEqual("B after", confirmedBlocked.entries.Single().title);
                Assert.AreNotEqual(oldUpdate, confirmedBlocked.last_update);
                AssertSummaryCells(summary, workspace.PlaylistSummaryView.Single(row => row.PlaylistId == blocked.playlist_id),
                    confirmedBlocked.last_update, Resources.Playlist_sync_status_updated);
                AssertSummaryCells(summary, workspace.PlaylistSummaryView.Single(row => row.PlaylistId == good.playlist_id),
                    confirmed.last_update, Resources.Playlist_sync_status_updated);
                AssertSummaryCells(summary, workspace.PlaylistSummaryView.Single(row => row.PlaylistId == bad.playlist_id),
                    oldUpdate, Resources.Playlist_sync_status_header);
                nextMutation = workspace.ResyncPlaylistTableAsync(confirmed);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(nextMutation, "summary-reload.next-explicit-mutation");
                Assert.AreEqual(PlaylistSyncStatusKind.Ok, workspace.CapturePlaylistSyncStatusSnapshot()["id:7531"].Kind);
            }
            catch (Exception failure) { primaryFailure = failure; throw; }
            finally
            {
                server?.ReleaseResponse();
                var cleanupFailures = new List<Exception>();
                void Cleanup(Action action)
                {
                    try { action(); }
                    catch (Exception failure) { cleanupFailures.Add(failure); }
                }
                if (reload != null) { Cleanup(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(reload, "summary-reload.finally-reload")); }
                if (producer != null) { Cleanup(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(producer, "summary-reload.finally-producer")); }
                if (nextMutation != null) { Cleanup(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(nextMutation, "summary-reload.finally-next-mutation")); }
                if (server != null) { Cleanup(server.Dispose); }
                Cleanup(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.ViewModel.PlaylistWorkspace.WaitForPlaylistSummaryDataBuildIdleAsync(), "summary-reload.finally-summary"));
                Cleanup(fixture.Close);
                if (cleanupFailures.Count > 0)
                {
                    var cleanupFailure = new AggregateException("Summary reload fixture cleanup failed.", cleanupFailures);
                    if (primaryFailure != null) { primaryFailure.Data["SummaryReloadCleanupFailure"] = cleanupFailure.ToString(); }
                    else { throw cleanupFailure; }
                }
            }

            static void PumpSummary(PlaylistWorkspaceViewModel workspace, MainWindow window)
            {
                // shellのBackground予約、worker、別予約のNormal反映を別々に回収する。
                TestUiDispatcherHost.AwaitTaskOnDispatcher(window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task,
                    "summary-reload.shell-background");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(workspace.WaitForPlaylistSummaryDataBuildIdleAsync(), "summary-reload.summary-build");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task,
                    "summary-reload.summary-reflect");
                TestUiDispatcherHost.ProcessQueuedPresentation();
            }

            static void AssertSummaryCells(CustomTableView summary, PlaylistSummaryRow row, DateTime updated, string status)
            {
                Assert.IsTrue(summary.IsLoaded && summary.IsVisible);
                var owner = Window.GetWindow(summary);
                // 専用設定のnative配置復元後は表の高さが0になり得るため、観測する一行分だけ領域を確保する。
                // 全行の同時表示や固定の画面寸法を前提にせず、既存の選択・スクロールで対象行を可視化する。
                double missingHeight = summary.HeaderHeight + summary.RowHeight - summary.SurfaceHeight;
                if (missingHeight > 0)
                {
                    owner.Height = owner.ActualHeight + missingHeight;
                    owner.UpdateLayout();
                }
                summary.SelectRowsByPredicate(candidate => ReferenceEquals(candidate, row));
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsTrue(summary.ActualWidth > 0 && summary.ActualHeight > 0,
                    $"Summary must be arranged: table={summary.ActualWidth}x{summary.ActualHeight}, surface={summary.SurfaceWidth}x{summary.SurfaceHeight}, owner={owner.ActualWidth}x{owner.ActualHeight}, measureValid={summary.IsMeasureValid}, arrangeValid={summary.IsArrangeValid}, first={summary.FirstVisibleRowIndex}, capacity={summary.CalculateFullVisibleRowCapacity()}, rowHeight={summary.RowHeight}, headerHeight={summary.HeaderHeight}.");
                Assert.IsTrue(summary.ItemsSource.Contains(row));
                int index = summary.ItemsSource.IndexOf(row);
                Assert.IsTrue(index >= summary.FirstVisibleRowIndex
                    && index < summary.FirstVisibleRowIndex + summary.CalculateFullVisibleRowCapacity(),
                    $"Summary row must be visible: id={row.PlaylistId}, index={index}, first={summary.FirstVisibleRowIndex}, capacity={summary.CalculateFullVisibleRowCapacity()}, size={summary.ActualWidth}x{summary.ActualHeight}, rowHeight={summary.RowHeight}.");
                CustomTableColumnLayoutSnapshot columns = summary.GetColumnLayoutSnapshot();
                CustomTableColumn lastUpdate = columns.VisibleEntries.Single(entry => entry.Column.Id == "LastUpdate").Column;
                CustomTableColumn statusColumn = columns.VisibleEntries.Single(entry => entry.Column.Id == "Status").Column;
                Assert.AreEqual(updated.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.CurrentCulture), summary.GetCellValue(row, lastUpdate, out _).Text);
                Assert.AreEqual(status, summary.GetCellValue(row, statusColumn, out _).Text);
            }
        });
    }

    /// <summary>実HTTP応答を保持した手動・明示・全再同期中は別編集を拒否し、拒否要求を再実行せず、UI公開と進捗終端後の次の明示編集だけを受理します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void MainWindowPlaylistSync_UsesHttpAndUiTerminalBeforeAcceptingAnotherEdit()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            ActualMainWindowFixture? fixture = null;
            TaskCompletionSource<PlaylistWorkspaceMutationRejectedEventArgs>? busyRejection = null;
            bool closeNextMutationNotification = false;
            fixture = CreateActualMainWindowFixture(
                windowTest,
                closeNextMessage: () => closeNextMutationNotification);
            try
            {
                BMSTable localTable = CreateAdmissionFixtureTable();
                fixture.Playlist.BMSTables.Add(localTable);
                PersistFixtureTable(Path.Combine(fixture.Root, "data", "song.db"), localTable);

                PlaylistWorkspaceViewModel workspace = fixture.ViewModel.PlaylistWorkspace;
                workspace.MutationRejected += (_, request) =>
                {
                    if (request.Kind == PlaylistWorkspaceMutationKind.RenameFolder && request.IsBusy)
                    {
                        busyRejection?.TrySetResult(request);
                    }
                };

                TaskCompletionSource<object?> presentationTerminal = NewCompletion();
                int presentationThreadId = 0;
                workspace.PlaylistTablesPresentationChanged += (_, _) =>
                {
                    Interlocked.CompareExchange(
                        ref presentationThreadId,
                        Environment.CurrentManagedThreadId,
                        comparand: 0);
                    BMSTable? activeTarget = fixture!.Playlist.BMSTables
                        .FirstOrDefault(table => table?.playlist_id == fixture!.Table.playlist_id);
                    if (activeTarget?.entries?.Any(entry => entry?.title == "Manual result") == true)
                    {
                        presentationTerminal.TrySetResult(null);
                    }
                };

                BMSTable manualTarget = fixture.Table;
                using var manualServer = new BlockingPlaylistJsonServer(
                    "{\"name\":\"Manual server table\",\"symbol\":\"M\",\"data_url\":\"./score.json\",\"level_order\":[1]}",
                    "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Manual result\",\"artist\":\"Artist\",\"level\":\"1\"}]");
                manualTarget.Page_url = manualServer.PageUri;
                manualTarget.Header_url = manualServer.PageUri;
                manualTarget.Data_url = new Uri("./score.json", UriKind.Relative);
                manualTarget.EnableExternalSync();
                Assert.IsTrue(workspace.ContainsActivePlaylistTable(manualTarget));
                Assert.IsNotNull(manualTarget.Page_url);
                Assert.IsFalse(fixture.Playlist.IsPlaylistUpdating);

                Task manualReload = workspace.ResyncPlaylistTableAsync(manualTarget);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    Task.WhenAny(manualReload, manualServer.DataRequestAccepted.Task),
                    "MainWindowPlaylistWorkspaceWpfTests.manual-data-request");
                Assert.IsFalse(
                    manualReload.IsCompleted,
                    "Manual reload must remain admitted while the local HTTP score response is held.");

                closeNextMutationNotification = true;
                busyRejection = NewCompletion<PlaylistWorkspaceMutationRejectedEventArgs>();
                Task busyRename = workspace.RenameFolderAsync(
                    localTable,
                    PlaylistFolderNode.CreateFolder("Local"),
                    "Local while manual reload is active");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    busyRename,
                    "MainWindowPlaylistWorkspaceWpfTests.manual-busy-rename");
                closeNextMutationNotification = false;
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    busyRejection.Task,
                    "MainWindowPlaylistWorkspaceWpfTests.manual-busy-rejection");
                Assert.IsTrue(busyRejection!.Task.Result.IsBusy);
                Assert.AreEqual(PlaylistWorkspaceMutationKind.RenameFolder, busyRejection!.Task.Result.Kind);
                Assert.AreEqual("Local", localTable.entries.Single().folder);
                Assert.IsFalse(
                    localTable.entries.Any(entry => entry.folder == "Local while manual reload is active"),
                    "A rejected edit must not be replayed after the manual reload releases its admission.");

                BlockingPlaylistJsonServer.ResponseStage deferredResponse = manualServer.EnqueueResponse(
                    "{\"name\":\"Deferred server table\",\"symbol\":\"D\",\"data_url\":\"./score.json\",\"level_order\":[1]}",
                    "[{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Deferred result\",\"artist\":\"Artist\",\"level\":\"1\"}]");
                TaskCompletionSource<PlaylistExternalSyncCompletionEventArgs> deferredCompletion = NewCompletion<PlaylistExternalSyncCompletionEventArgs>();
                workspace.PlaylistExternalSyncCompleted += (_, request) =>
                {
                    if (request.Reason == "real_deferred_sync")
                    {
                        deferredCompletion.TrySetResult(request);
                    }
                };
                TaskCompletionSource<object?> deferredPresentation = NewCompletion();
                workspace.PlaylistTablesPresentationChanged += (_, _) =>
                {
                    BMSTable? activeTarget = fixture!.Playlist.BMSTables
                        .FirstOrDefault(table => table?.playlist_id == manualTarget.playlist_id);
                    if (activeTarget?.entries?.Any(entry => entry?.title == "Deferred result") == true)
                    {
                        deferredPresentation.TrySetResult(null);
                    }
                };

                var deferredProgress = new ConcurrentQueue<PlaylistSyncProgressSnapshot>();
                TaskCompletionSource<object?> deferredProgressTerminal = NewCompletion();
                workspace.PlaylistSyncProgressChanged += (_, request) =>
                {
                    deferredProgress.Enqueue(request.Snapshot);
                    if (request.Snapshot.Source == "external_playlist_sync" && !request.Snapshot.IsActive)
                    { deferredProgressTerminal.TrySetResult(null); }
                };
                PlaylistExternalSyncCompletionEventArgs? skipped = null;
                workspace.PlaylistExternalSyncCompleted += (_, result) =>
                {
                    if (result.Reason == "busy_optional_sync") { skipped = result; }
                };
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    workspace.RunExternalPlaylistSyncAsync("busy_optional_sync", false, true, 0),
                    "MainWindowPlaylistWorkspaceWpfTests.busy-optional-sync");
                Assert.IsNotNull(skipped);
                Assert.IsTrue(skipped.WasSkipped);
                Assert.IsFalse(deferredResponse.DataRequestAccepted.Task.IsCompleted);

                manualServer.ReleaseResponse();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    manualReload,
                    "MainWindowPlaylistWorkspaceWpfTests.manual-reload");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    presentationTerminal.Task,
                    "MainWindowPlaylistWorkspaceWpfTests.manual-ui-terminal");
                Assert.AreEqual(
                    TestUiDispatcherHost.Dispatcher.Thread.ManagedThreadId,
                    Volatile.Read(ref presentationThreadId),
                    "Playlist table presentation must finish on the shared UI dispatcher.");

                BMSTable reloadedTarget = fixture.Playlist.BMSTables
                    .Single(table => table.playlist_id == manualTarget.playlist_id);
                Assert.AreNotSame(manualTarget, reloadedTarget);
                Assert.AreEqual("Manual result", reloadedTarget.entries.Single().title);
                Assert.IsFalse(deferredResponse.DataRequestAccepted.Task.IsCompleted, "Busy要求を解放後に自動実行しません。");
                Task explicitSync = workspace.RunExternalPlaylistSyncAsync("real_deferred_sync", false, true, 0);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    Task.WhenAny(deferredResponse.DataRequestAccepted.Task, deferredCompletion.Task),
                    "MainWindowPlaylistWorkspaceWpfTests.deferred-data-request");
                Assert.IsTrue(
                    deferredResponse.DataRequestAccepted.Task.IsCompleted,
                    "The accepted deferred sync must reach its real HTTP request after the manual terminal.");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    deferredResponse.DataRequestAccepted.Task,
                    "MainWindowPlaylistWorkspaceWpfTests.deferred-data-request-signal");
                Assert.IsFalse(
                    deferredCompletion.Task.IsCompleted,
                    "Deferred sync must retain its accepted request while the HTTP score response is held.");

                closeNextMutationNotification = true;
                busyRejection = NewCompletion<PlaylistWorkspaceMutationRejectedEventArgs>();
                Task deferredBusyRename = workspace.RenameFolderAsync(
                    localTable,
                    PlaylistFolderNode.CreateFolder("Local"),
                    "Local while deferred sync is active");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    deferredBusyRename,
                    "MainWindowPlaylistWorkspaceWpfTests.deferred-busy-rename");
                closeNextMutationNotification = false;
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    busyRejection.Task,
                    "MainWindowPlaylistWorkspaceWpfTests.deferred-busy-rejection");
                Assert.IsTrue(busyRejection!.Task.Result.IsBusy);
                Assert.AreEqual(PlaylistWorkspaceMutationKind.RenameFolder, busyRejection!.Task.Result.Kind);
                Assert.AreEqual("Local", localTable.entries.Single().folder);

                deferredResponse.ReleaseResponse();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    deferredCompletion.Task,
                    "MainWindowPlaylistWorkspaceWpfTests.deferred-completion");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    deferredPresentation.Task,
                    "MainWindowPlaylistWorkspaceWpfTests.deferred-ui-terminal");
                Assert.IsTrue(deferredCompletion.Task.Result.Succeeded);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(deferredProgressTerminal.Task, "MainWindowPlaylistWorkspaceWpfTests.deferred-progress-terminal");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                PlaylistSyncProgressSnapshot[] notifications = deferredProgress.Where(snapshot => snapshot.Source == "external_playlist_sync").ToArray();
                Assert.IsTrue(notifications.Length > 0);
                Assert.IsTrue(notifications.All(snapshot => snapshot.Source == "external_playlist_sync"));
                Assert.IsTrue(notifications.All(snapshot => snapshot.OperationId == 0));
                Assert.IsTrue(notifications.All(snapshot => snapshot.Request is { } request
                    && request.OperationToken == 0 && request.Source == "external_playlist_sync" && request.Version > 0));
                Assert.IsTrue(notifications.All(snapshot => snapshot.Request == notifications[0].Request));
                Assert.IsFalse(fixture.ViewModel.ProgressHub.Rows.Any(row => row.Key.StartsWith("playlist:external_playlist_sync:", StringComparison.Ordinal)));
                Assert.IsTrue(notifications.Any(snapshot => snapshot.IsActive
                    && snapshot.CurrentUri?.AbsoluteUri == manualServer.PageUri.AbsoluteUri));
                Assert.IsTrue(notifications.Any(snapshot => snapshot.IsActive && snapshot.CompletedTableCount > 0));
                Assert.IsFalse(notifications[^1].IsActive);

                TestUiDispatcherHost.AwaitTaskOnDispatcher(explicitSync, "MainWindowPlaylistWorkspaceWpfTests.explicit-sync-actual-terminal");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.Playlist.WaitForPlaylistMutationIdleAsync(),
                    "MainWindowPlaylistWorkspaceWpfTests.playlist-actual-terminal");

                closeNextMutationNotification = true;
                try
                {
                    awaitOnDispatcher(
                        workspace.RenameFolderAsync(
                            localTable,
                            PlaylistFolderNode.CreateFolder("Local"),
                            "Local after deferred terminal"),
                        "MainWindowPlaylistWorkspaceWpfTests.deferred-retry");
                }
                finally
                {
                    closeNextMutationNotification = false;
                }
                Assert.AreEqual("Local after deferred terminal", localTable.entries.Single().folder);

                // MainWindow の実再読込み入口から、要求受付・HTTP・Owner・Hub まで接続する。
                BlockingPlaylistJsonServer.ResponseStage reloadResponse = manualServer.EnqueueResponse(
                    "{\"name\":\"Reloaded table\",\"symbol\":\"R\",\"data_url\":\"./score.json\",\"level_order\":[1]}",
                    "[{\"md5\":\"cccccccccccccccccccccccccccccccc\",\"title\":\"ReloadTables result\",\"artist\":\"Artist\",\"level\":\"1\"}]");
                TaskCompletionSource<PlaylistExternalSyncCompletionEventArgs> reloadCompletion = NewCompletion<PlaylistExternalSyncCompletionEventArgs>();
                workspace.PlaylistExternalSyncCompleted += (_, request) =>
                {
                    if (request.Reason == "ReloadTables") { reloadCompletion.TrySetResult(request); }
                };
                TaskCompletionSource<object?> reloadProgressTerminal = NewCompletion();
                workspace.PlaylistSyncProgressChanged += (_, request) =>
                {
                    if (request.Snapshot.Source == "external_playlist_sync" && !request.Snapshot.IsActive)
                    { reloadProgressTerminal.TrySetResult(null); }
                };
                Task reloadOperation = fixture.ViewModel.ReloadTablesAsync();
                try
                {
                    Task firstReloadOutcome = Task.WhenAny(reloadResponse.DataRequestAccepted.Task, reloadOperation);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(firstReloadOutcome, "MainWindowPlaylistWorkspaceWpfTests.reload-tables-arrival");
                    if (reloadOperation.IsCompleted) { TestUiDispatcherHost.AwaitTaskOnDispatcher(reloadOperation, "reload-tables-early-failure"); }
                    Assert.IsTrue(reloadResponse.DataRequestAccepted.Task.IsCompleted);
                    Assert.IsFalse(reloadOperation.IsCompleted, "通信から必要出力・公開まで同じPの実Taskで待ちます。");
                    Assert.IsFalse(fixture.Playlist.TryEnterPlaylistMutation(out _));
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    StartupProgressWorkflowOwner progress = fixture.ViewModel.ProgressHub.StartupProgress;
                    long operationToken = progress.GetActiveStartupProgressOperationToken();
                    Assert.IsTrue(operationToken > 0);
                    OperationProgressRow progressRow = fixture.ViewModel.ProgressHub.Rows.Single(row =>
                        row.Key.StartsWith("background:external_playlist_sync:", StringComparison.Ordinal)
                        || row.Key.StartsWith("playlist:external_playlist_sync:", StringComparison.Ordinal));
                    Assert.AreEqual("startup", progressRow.ParentKey);
                    StringAssert.Contains(progressRow.Label, Resources.Statusbar_progress_task_external_playlist_sync);
                    Assert.AreEqual(Resources.Statusbar_progress_reload_tables, progress.Label);
                    reloadResponse.ReleaseResponse();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(reloadOperation, "MainWindowPlaylistWorkspaceWpfTests.reload-tables-operation");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(reloadCompletion.Task, "MainWindowPlaylistWorkspaceWpfTests.reload-tables-completion");
                    Assert.IsTrue(reloadCompletion.Task.Result.Succeeded);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(reloadProgressTerminal.Task, "MainWindowPlaylistWorkspaceWpfTests.reload-tables-progress-terminal");
                    PlaylistSyncProgressSnapshot[] captured = deferredProgress.Where(snapshot => snapshot.Source == "external_playlist_sync"
                        && snapshot.Request?.OperationToken == operationToken).ToArray();
                    Assert.IsTrue(captured.Length > 0);
                    Assert.IsTrue(captured.All(snapshot => snapshot.Request == captured[0].Request));
                    Assert.IsFalse(captured[^1].IsActive);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.IsFalse(fixture.ViewModel.ProgressHub.Rows.Any(row => row.Key.StartsWith("playlist:external_playlist_sync:", StringComparison.Ordinal)));
                }
                finally
                {
                    reloadResponse.ReleaseResponse();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(reloadOperation, "MainWindowPlaylistWorkspaceWpfTests.reload-tables-drain");
                }

            }
            finally
            {
                closeNextMutationNotification = false;
                ActualMainWindowFixture closingFixture = fixture!;
                fixture = null;
                closingFixture.Close();
            }

            static void awaitOnDispatcher(Task task, string operationName)
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(task, operationName);
            }
        });
    }

    /// <summary>compiled表再読込みクリックは未完了Taskでも処理済みにし、通常失敗と所有されない取消を一回通知し、終了取消・既通知警告を静穏に終えます。</summary>
    [DataTestMethod]
    [DataRow("success")] // 成功の既存接続と即時Handledを維持します。
    [DataRow("failure")] // 非同期の普通の失敗は既存通知へ一回渡します。
    [DataRow("cancellation")] // 終了要求のない取消を実失敗として通知します。
    [DataRow("shutdown-cancellation")] // 実Window終了で所有された取消だけを通知しません。
    [DataRow("preflight")] // 管理主体が通知済みの専用警告を重ねません。
    public void PlaylistRootReload_UsesOneTypedTerminalAndHandlesBeforeAsyncCompletion(string terminalOutcome)
    {
        bool shutdown = terminalOutcome == "shutdown-cancellation";
        int expectedNotifications = terminalOutcome is "failure" or "cancellation" ? 1 : 0;
        TaskCompletionSource<object?> completion = NewCompletion();
        var dialogs = new RecordingPlaylistWorkspaceDialogService();
        int callCount = 0;
        MainWindowPlaylistTablesReloadTerminal reloadTerminal =
            new(() => ++callCount == 1 ? completion.Task : Task.CompletedTask);
        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (viewModel, window) =>
            {
                Task reloadEvent = Task.CompletedTask;
                try
                {
                    var menu = (ContextMenu)window.FindResource("treeViewPlaylistRootContextMenu");
                    MenuItem reload = FindMenuItemByHeaderBindingPath(menu, "Resources.Reload");
                    RoutedEventArgs? args = null;
                    reloadEvent = MainWindowPresentationTestHarness.CaptureAsyncEventCompletion(() => args = RaiseMenuClick(reload));
                    Assert.IsNotNull(args);
                    Assert.IsTrue(args.Handled);
                    Assert.AreEqual(1, callCount);
                    Assert.IsFalse(completion.Task.IsCompleted);
                    if (shutdown)
                    {
                        window.Close();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync(), "compiled-playlist-reload-owned-shutdown");
                        Assert.IsTrue(viewModel.ShellShutdownWorkflow.IsShutdownRequested);
                    }
                    switch (terminalOutcome)
                    {
                        case "failure": completion.TrySetException(new IOException("playlist root reload failed")); break;
                        case "cancellation":
                        case "shutdown-cancellation": completion.TrySetCanceled(); break;
                        case "preflight":
                            completion.TrySetException(new LibraryDirectoryPreflightException(
                            LibraryDirectoryPreflightUse.BmsRoot, @"C:\missing-root", LibraryDirectoryPreflightFailureCause.NotFound, "missing")); break;
                        default: completion.TrySetResult(null); break;
                    }
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(reloadEvent, "compiled-playlist-reload-event-terminal");
                    Assert.AreEqual(expectedNotifications, dialogs.MessageRequests.Count);
                    if (terminalOutcome == "failure")
                    {
                        Assert.IsTrue(dialogs.MessageShown.Task.IsCompletedSuccessfully);
                        Assert.AreEqual(Resources.Msg_error_unexpected, dialogs.MessageRequests.Single().MessageBoxText);
                    }
                    if (expectedNotifications != 0)
                    {
                        Assert.AreEqual(Resources.Msg_error_unexpected, dialogs.MessageRequests.Single().MessageBoxText);
                    }
                    if (!shutdown)
                    {
                        Assert.IsTrue(RaiseMenuClick(reload).Handled);
                        Assert.AreEqual(2, callCount);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.AreEqual(expectedNotifications, dialogs.MessageRequests.Count);
                    }
                }
                finally
                {
                    completion.TrySetResult(null);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(reloadEvent, "compiled-playlist-reload-event-cleanup");
                    if (shutdown)
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(window.CloseCompletion, "compiled-playlist-reload-close-cleanup");
                    }
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                }
            },
            playlistWorkspaceTerminals: CreateTerminals(tablesReload: reloadTerminal),
            playlistWorkspaceDialogService: dialogs);
    }

    /// <summary>プレイリストルートの実コンテキストメニューは所有モーダルを開き、取消・Escape・native Close・親終了でDataContextと全購読を回収します。再表示は入力・検索・選択が空で既定フィルターへ戻ります。</summary>
    [DataTestMethod]
    [DataRow("cancel")]
    [DataRow("escape")]
    [DataRow("native")]
    [DataRow("owner")]
    public void PlaylistImportDialog_UsesOwnedModalAndDiscardsSessionOnEveryClose(string closeKind)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            ActualMainWindowFixture fixture = CreateActualMainWindowFixture(scope);
            try
            {
                ModalObservation<PlaylistImportDialog> observation = OpenImportDialog(fixture, "import-" + closeKind, (dialog, _) =>
                {
                    var vm = (PlaylistImportDialogViewModel)dialog.DataContext;
                    vm.UriInput = "https://example.test/unsubmitted";
                    vm.SearchText = "draft";
                    vm.HideRegisteredUrls = false;
                    vm.HideSameNames = true;
                    vm.SetSelected(new(new("https://example.test/selected"), "Draft"), true);
                    switch (closeKind)
                    {
                        case "cancel": RaiseButtonClick((Button)dialog.FindName("cancelImportButton")); break;
                        case "escape":
                            PresentationSource source = PresentationSource.FromVisual(dialog) ?? throw new AssertFailedException("実表示のPresentationSourceがありません。");
                            dialog.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.Escape) { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
                            break;
                        case "native": dialog.Close(); break;
                        case "owner": fixture.Window.Close(); break;
                    }
                });
                Assert.AreSame(fixture.Window, observation.Owner);
                Assert.IsFalse(observation.OwnerEnabled, "モーダル中は親のnative HWNDが無効であることを観測する。");
                Assert.IsNull(observation.Window.DataContext);
                Assert.AreEqual(1, observation.DataContextDetachCount);
                var oldSession = (PlaylistImportDialogViewModel)observation.DataContext;
                int oldNotifications = 0;
                oldSession.PropertyChanged += (_, _) => oldNotifications++;
                Task catalog = fixture.ViewModel.PlaylistWorkspace.LoadExternalTableCollectionAsync(new Uri("https://example.test/catalog"), (_, _) => Task.FromResult<IReadOnlyList<BMSTableSimple>>([]));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(catalog, "import-closed-catalog-update");
                ResourceService.Current.ChangeCulture(Resources.Culture?.Name ?? "en-US");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(0, oldNotifications, "閉鎖セッションへカタログ・言語通知を配送しない。");
                if (closeKind == "owner")
                {
                    TestUiDispatcherHost.AwaitPresentationOnDispatcher(fixture.Window.CloseCompletion, "import-owner-close");
                    Assert.AreEqual(1, fixture.Lifetime.RequestShutdownCount);
                }
                else
                {
                    Assert.IsTrue(IsNativeWindowEnabled(new WindowInteropHelper(fixture.Window).Handle));
                    OpenImportDialog(fixture, "import-reopen", (dialog, _) =>
                    {
                        var fresh = (PlaylistImportDialogViewModel)dialog.DataContext;
                        Assert.AreEqual(0, fresh.SelectedCount);
                        Assert.AreEqual("", fresh.UriInput);
                        Assert.AreEqual("", fresh.SearchText);
                        Assert.IsTrue(fresh.HideRegisteredUrls);
                        Assert.IsFalse(fresh.HideSameNames);
                        dialog.Close();
                    });
                }
            }
            finally { fixture.Close(); }
        });
    }

    /// <summary>実WindowのStarted後に子の閉鎖と購読回収を先に完了しても、実HTTP本文待ちの取込みはWorkspaceで継続します。同URL除外を含む結果応答の前にPを解放し、DB登録・BMT出力と結果発行も確認します。</summary>
    [TestMethod]
    public void PlaylistImportDialog_StartedClosesBeforeWorkspaceImportFinishes()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string output = "";
            Task? idle = null;
            ActualMainWindowFixture? presenterFixture = null;
            TaskCompletionSource<object?> responseProbe = NewCompletion<object?>();
            ActualMainWindowFixture fixture = CreateActualMainWindowFixture(scope,
                messagePresenter: (owner, request) =>
                {
                    if (request.Caption == Resources.Playlist_import_result_title)
                    {
                        try
                        {
                            ActualMainWindowFixture active = presenterFixture ?? throw new AssertFailedException("結果表示より前にfixtureを作成します。");
                            Task terminal = idle ?? throw new AssertFailedException("HTTP本文gate中に実P終端Taskを捕捉します。");
                            Assert.AreSame(active.Window, owner);
                            Assert.AreSame(active.Window, request.Owner);
                            StringAssert.Contains(request.MessageBoxText, string.Format(Resources.Playlist_import_result_summary_format, 1, 1, 0, 0));
                            // presenterからまだ応答していない間に、実処理がPを解放して次の明示操作を許可する。
                            TestUiDispatcherHost.AwaitPresentationOnDispatcher(terminal, "import-admission-released-before-summary-response");
                            Assert.IsTrue(active.Playlist.TryEnterPlaylistMutation(out IDisposable admission));
                            admission.Dispose();
                            responseProbe.TrySetResult(null);
                        }
                        catch (Exception failure)
                        {
                            // coordinatorがFailedへ変換しても、検証失敗をObserveFaultへ吸収させない。
                            responseProbe.TrySetException(failure);
                        }
                    }
                    return new ThemedMessageBoxResponse(MessageBoxResult.OK, closedWithoutSelection: false);
                },
                bmtOptionsProvider: () => new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = true, BeatorajaBmtTablePath = output });
            presenterFixture = fixture;
            var existingUri = new Uri("https://example.test/already-registered");
            fixture.Table.Page_url = existingUri;
            output = Path.Combine(fixture.Root, "import-bmt");
            Directory.CreateDirectory(output);
            using var server = new BlockingPlaylistJsonServer(
                "{\"name\":\"Imported after dialog close\",\"symbol\":\"I\",\"data_url\":\"./score.json\"}",
                "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Imported chart\",\"level\":\"1\"}]");
            PlaylistWorkspaceViewModel workspace = fixture.ViewModel.PlaylistWorkspace;
            TaskCompletionSource<ExternalPlaylistImportQueueSummary> summary = NewCompletion<ExternalPlaylistImportQueueSummary>();
            EventHandler<ExternalPlaylistImportQueueSummaryReadyEventArgs> summaryHandler = (_, e) => summary.TrySetResult(e.Summary);
            workspace.ExternalPlaylistImportQueueSummaryReady += summaryHandler;
            Exception? primaryFailure = null;
            try
            {
                ModalObservation<PlaylistImportDialog> observation = OpenImportDialog(fixture, "import-start", (dialog, _) =>
                {
                    var vm = (PlaylistImportDialogViewModel)dialog.DataContext;
                    vm.SetSelected(new(existingUri, "Existing"), true);
                    vm.SetSelected(new(server.PageUri, "Import"), true);
                    RaiseButtonClick((Button)dialog.FindName("startImportButton"));
                });
                Assert.IsNull(observation.Window.DataContext);
                Assert.IsTrue(fixture.Window.IsEnabled);
                idle = fixture.Playlist.WaitForPlaylistMutationIdleAsync();
                Task producer = Task.WhenAny(idle, server.Completion).Unwrap();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(server.DataRequestAccepted.Task, producer, "import-http-gate"), "import-http-gate");
                Assert.IsFalse(idle.IsCompleted);
                Assert.IsFalse(summary.Task.IsCompleted);
                server.ReleaseResponse();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(idle, "import-workspace-terminal");
                Assert.IsTrue(summary.Task.IsCompleted, "結果イベントを実処理終端までに発行する。");
                Assert.AreEqual(1, summary.Task.Result.ImportedCount);
                Assert.AreEqual(1, summary.Task.Result.SkippedDuplicateUrlCount);
                // idleが先に終わる順序も正当なので、結果表示consumerは独立して回収します。
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(responseProbe.Task, "import-summary-response-probe");
                using (LR2SongDBExtended read = new BmsLibraryDbGateway(Path.Combine(fixture.Root, "data", "song.db")).OpenSongDbReadOnly())
                {
                    Assert.IsTrue(read.Table<LR2SongDBExtended.playlist>().Any(x => x.name == "Imported after dialog close"));
                }

                Assert.IsTrue(Directory.GetFiles(output, "*.bmt", SearchOption.AllDirectories).Length > 0);
            }
            catch (Exception exception) { primaryFailure = exception; throw; }
            finally
            {
                try
                {
                    server.ReleaseResponse();
                    try
                    {
                        if (idle != null)
                        {
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(idle, "import-finally-terminal");
                            TestUiDispatcherHost.AwaitPresentationOnDispatcher(responseProbe.Task, "import-finally-summary-response-probe");
                        }
                    }
                    finally
                    {
                        workspace.ExternalPlaylistImportQueueSummaryReady -= summaryHandler;
                        fixture.Close();
                    }
                }
                catch when (primaryFailure != null) { }
            }
        });
    }

    /// <summary>実LR2必須出力のI/O失敗後も確定DBを保持し、同封receiptの警告へ応答する前に実処理とPを解放します。警告・集計の所有者と事実を実完了イベントへ照合します。</summary>
    [TestMethod]
    public void PlaylistImport_Lr2OutputFailurePreservesCommittedDatabaseAndReleasesAdmissionBeforeWarningResponse()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            const string tableName = "ImportOutputFailure";
            const string chartTitle = "Committed chart after LR2 output failure";
            string chartMd5 = new('a', 32);
            string outputDirectory = "";
            Task? idle = null;
            ActualMainWindowFixture? presenterFixture = null;
            TaskCompletionSource<ExternalPlaylistImportQueueSummaryReadyEventArgs> completion = NewCompletion<ExternalPlaylistImportQueueSummaryReadyEventArgs>();
            TaskCompletionSource<object?> warningProbe = NewCompletion<object?>();
            TaskCompletionSource<object?> summaryProbe = NewCompletion<object?>();
            int completionCount = 0;
            int warningCalls = 0;
            int summaryCalls = 0;
            ActualMainWindowFixture fixture = CreateActualMainWindowFixture(scope,
                configureSettings: values =>
                {
                    string root = values.BMSRootPath;
                    values.OperationModeLR2DB = true;
                    values.LR2RootPath = root;
                    values.LR2SongDBPath = Path.Combine(root, "data", "song.db");
                    values.LR2ConfigXmlPath = Path.Combine(root, "LR2files", "Config", "config.xml");
                    Directory.CreateDirectory(Path.GetDirectoryName(values.LR2ConfigXmlPath) ?? throw new AssertFailedException());
                    File.WriteAllText(values.LR2ConfigXmlPath, "<config><system /><jukebox /></config>");
                    string songs = Path.Combine(root, "Songs");
                    Directory.CreateDirectory(songs);
                    values.BMSRootPath = songs;
                    values.BMSInstallDir = songs;
                    var config = new LR2Config(values.LR2ConfigXmlPath);
                    config.AddBMSSearchDirectories([songs]);
                    config.Save(config.ConfigFilePath);
                    File.WriteAllBytes(Path.Combine(root, "LR2body.exe"), []);
                    values.LR2CustomFolderOutputBaseDir = Path.Combine(root, "custom");
                    values.LR2CustomFolderOutputBaseDirRootType = Path.Combine(root, "root-custom");
                    values.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
                    values.PlaylistDefaultIgnoreFolderOutput = (int)(LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                        & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder);
                    Directory.CreateDirectory(values.LR2CustomFolderOutputBaseDir);
                    Directory.CreateDirectory(values.LR2CustomFolderOutputBaseDirRootType);
                },
                messagePresenter: (owner, request) =>
                {
                    bool isSummary = request.Caption == Resources.Playlist_import_result_title;
                    TaskCompletionSource<object?> probe = isSummary ? summaryProbe : warningProbe;
                    try
                    {
                        ActualMainWindowFixture active = presenterFixture ?? throw new AssertFailedException("通知より前にfixtureを作成します。");
                        Task terminal = idle ?? throw new AssertFailedException("HTTP本文gate中に実P終端を捕捉します。");
                        if (!isSummary)
                        {
                            // 旧同期警告経路では完了イベントも未発行のため、まず実P終端を期限付きで観測します。
                            TestUiDispatcherHost.AwaitPresentationOnDispatcher(terminal, "import-output-failure-admission-before-warning-response");
                        }
                        Assert.IsTrue(completion.Task.IsCompleted, "P解放より前にreceiptと集計の完了イベントを発行する。");
                        ExternalPlaylistImportQueueSummaryReadyEventArgs published = completion.Task.GetAwaiter().GetResult();
                        Assert.AreSame(active.Window, owner);
                        Assert.AreSame(active.Window, request.Owner);
                        if (isSummary)
                        {
                            summaryCalls++;
                            Assert.IsTrue(warningProbe.Task.IsCompletedSuccessfully, "警告の応答後に集計を表示する。");
                            Assert.AreEqual(MessageBoxImage.Exclamation, request.Icon);
                            StringAssert.Contains(request.MessageBoxText, string.Format(Resources.Playlist_import_result_summary_format, 0, 0, 0, 1));
                            ExternalPlaylistImportOutcome failure = published.Summary.FailedOutcomes.Single();
                            StringAssert.Contains(request.MessageBoxText, failure.Uri.AbsoluteUri);
                        }
                        else
                        {
                            warningCalls++;
                            Assert.IsFalse(published.Receipt.IsEmpty);
                            PlaylistOperationNotificationOwner.OperationNotification notification = published.Receipt.Notifications.Single();
                            Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning, notification.Severity);
                            Assert.AreEqual(Resources.MessageBoxTitle_Warning, notification.Caption);
                            Assert.AreEqual(string.Format(Resources.Warn_CustomFolderOutputFailed, tableName, outputDirectory), notification.Message);
                            Assert.AreEqual(notification.Caption, request.Caption);
                            Assert.AreEqual(notification.Message, request.MessageBoxText);
                            Assert.AreEqual(MessageBoxImage.Exclamation, request.Icon);
                            // 警告presenterの応答前でも、別の明示変更を受け付ける。
                            Assert.IsTrue(active.Playlist.TryEnterPlaylistMutation(out IDisposable admission));
                            admission.Dispose();
                            using LR2SongDBExtended read = new BmsLibraryDbGateway(Path.Combine(active.Root, "data", "song.db")).OpenSongDbReadOnly();
                            LR2SongDBExtended.playlist persisted = read.Table<LR2SongDBExtended.playlist>().Single(row => row.name == tableName);
                            int persistedId = persisted.playlist_id ?? throw new AssertFailedException("確定した表IDがありません。");
                            ExternalPlaylistImportOutcome failure = published.Summary.FailedOutcomes.Single();
                            // 直接JSONの取得元はヘッダーURLとして保存し、存在しないHTMLページURLを生成しません。
                            Assert.AreEqual(failure.Uri.AbsoluteUri, persisted.header_url);
                            Assert.IsTrue(string.IsNullOrEmpty(persisted.page_url));
                            LR2SongDBExtended.playlist_entry entry = read.Table<LR2SongDBExtended.playlist_entry>().Single(row => row.playlist_id == persistedId);
                            Assert.AreEqual(persistedId, entry.playlist_id);
                            Assert.AreEqual(chartMd5, entry.md5);
                            Assert.AreEqual(chartTitle, entry.title);
                            Assert.AreEqual(7d, entry.level);
                        }
                        probe.TrySetResult(null);
                    }
                    catch (Exception failure)
                    {
                        // coordinatorのFailed変換や本番ObserveFaultで表明失敗を失わない。
                        probe.TrySetException(failure);
                    }
                    return new ThemedMessageBoxResponse(MessageBoxResult.OK, closedWithoutSelection: false);
                });
            presenterFixture = fixture;
            outputDirectory = Path.Combine(fixture.Root, "custom", tableName);
            Directory.CreateDirectory(outputDirectory);
            FileStream? outputBlocker = null;
            var server = new BlockingPlaylistJsonServer(
                "{\"name\":\"" + tableName + "\",\"symbol\":\"W\",\"output_dir\":\"" + tableName + "\",\"data_url\":\"./score.json\"}",
                "[{\"md5\":\"" + chartMd5 + "\",\"title\":\"" + chartTitle + "\",\"level\":\"7\"}]");
            PlaylistWorkspaceViewModel workspace = fixture.ViewModel.PlaylistWorkspace;
            EventHandler<ExternalPlaylistImportQueueSummaryReadyEventArgs> completed = (_, request) =>
            {
                completionCount++;
                completion.TrySetResult(request);
            };
            workspace.ExternalPlaylistImportQueueSummaryReady += completed;
            Exception? primaryFailure = null;
            try
            {
                outputBlocker = new FileStream(Path.Combine(outputDirectory, "0000.lr2folder"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                PlaylistImportStartResult start = workspace.TryStartPlaylistImport([server.PageUri]);
                Assert.AreEqual(PlaylistImportStartStatus.Started, start.Status);
                idle = fixture.Playlist.WaitForPlaylistMutationIdleAsync();
                Task producer = Task.WhenAny(idle, server.Completion).Unwrap();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    TestUiDispatcherHost.AwaitNotificationAsync(server.DataRequestAccepted.Task, producer, "import-output-failure-http-gate"),
                    "import-output-failure-http-gate");
                Assert.IsFalse(idle.IsCompleted);
                server.ReleaseResponse();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(idle, "import-output-failure-terminal");
                Assert.IsTrue(completion.Task.IsCompleted, "実処理終端までにreceiptと集計を同じイベントで発行する。");
                Assert.AreEqual(1, completionCount, "実処理終端までの完了イベントは一回です。");
                ExternalPlaylistImportQueueSummaryReadyEventArgs published = completion.Task.GetAwaiter().GetResult();
                Assert.IsFalse(published.Receipt.IsEmpty);
                Assert.AreEqual(0, published.Summary.ImportedCount);
                Assert.AreEqual(0, published.Summary.SkippedDuplicateUrlCount);
                Assert.AreEqual(0, published.Summary.SkippedDuplicateNameCount);
                Assert.AreEqual(1, published.Summary.FailedCount);
                Assert.AreEqual(server.PageUri.AbsoluteUri, published.Summary.FailedOutcomes.Single().Uri.AbsoluteUri);
                // idleとUI consumerの完了順を固定せず、警告と集計をそれぞれ回収する。
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(warningProbe.Task, "import-output-failure-warning-probe");
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(summaryProbe.Task, "import-output-failure-summary-probe");
                Assert.AreEqual(1, warningCalls);
                Assert.AreEqual(1, summaryCalls);
            }
            catch (Exception failure) { primaryFailure = failure; throw; }
            finally
            {
                server.ReleaseResponse();
                try
                {
                    if (idle != null)
                    {
                        try
                        {
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(idle, "import-output-failure-finally-terminal");
                            TestUiDispatcherHost.AwaitPresentationOnDispatcher(warningProbe.Task, "import-output-failure-finally-warning");
                        }
                        finally
                        {
                            TestUiDispatcherHost.AwaitPresentationOnDispatcher(summaryProbe.Task, "import-output-failure-finally-summary");
                        }
                    }
                }
                catch when (primaryFailure != null) { }
                finally
                {
                    workspace.ExternalPlaylistImportQueueSummaryReady -= completed;
                    try { outputBlocker?.Dispose(); }
                    catch when (primaryFailure != null) { }
                    finally
                    {
                        try { fixture.Close(); }
                        catch when (primaryFailure != null) { }
                        finally
                        {
                            try { server.Dispose(); }
                            catch when (primaryFailure != null) { }
                        }
                    }
                }
            }
        });
    }

    /// <summary>実WPFの通常・未所持メニューの表示とClick接続を通し、単体右クリックの固定行、本体・差分区分、複数行の選択順と選択外の対象決定を確認します。セルクリックを別入口へ渡す接続もここで保証し、取得判断の条件網羅は責務側へ分担します。</summary>
    [TestMethod]
    public void PlaylistUrlBulkImport_IsOwnedByWorkspaceAndShellForwarded()
    {
        TaskCompletionSource<object?> singleCompletion = NewCompletion();
        TaskCompletionSource<object?> bulkCompletion = NewCompletion();
        var singleUrls = new List<Uri>();
        var availabilityCalls = new List<(object Context, IReadOnlyList<object> Rows)>();
        var bulkCalls = new List<(IReadOnlyList<object> Rows, bool IsDiff)>();
        var externalLookupCalls = new List<IReadOnlyList<object>>();
        int expansionCalls = 0;
        PlaylistUrlInstallTreeExpansionEventSourceFake expansionEventSource = new();
        BMSTable playlistTable = new() { name = "URL playlist" };
        PlaylistDetailRow firstRow = CreatePlaylistUrlRow(
            playlistTable,
            "33333333333333333333333333333333",
            "https://example.invalid/main-first.zip",
            "https://example.invalid/diff-first.zip");
        PlaylistDetailRow secondRow = CreatePlaylistUrlRow(
            playlistTable,
            "44444444444444444444444444444444",
            "https://example.invalid/main-second.zip",
            "https://example.invalid/diff-second.zip");
        MainWindowPlaylistUrlAcquisitionTerminal urlAcquisition = new(
            url =>
            {
                singleUrls.Add(url);
                return singleCompletion.Task;
            },
            (rows, isDiff) =>
            {
                bulkCalls.Add((rows, isDiff));
                return bulkCompletion.Task;
            },
            rows =>
            {
                externalLookupCalls.Add(rows);
                return Task.CompletedTask;
            },
            (contextRow, rows) =>
            {
                availabilityCalls.Add((contextRow, rows));
                return new PlaylistUrlContextMenuAvailability(
                    isPlaylistContext: true,
                    isBulkContext: rows.Count > 1,
                    canOpenUrl: true,
                    canOpenDiffUrl: true,
                    canFindExternalPackage: true);
            });
        MainWindowPlaylistUrlInstallTreeExpansionTerminal expansion = new(() =>
        {
            Assert.IsTrue(bulkCompletion.Task.IsCompleted);
            expansionCalls++;
        });

        try
        {
            MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
                MainWindowViewModelTestFactory.CreateIsolatedSettings(),
                (scope, viewModel, window) =>
                {
                    try
                    {
                        var table = (CustomTableView)window.FindName("customTableView");
                        table.ItemsSource = new List<object> { firstRow, secondRow };
                        table.SelectRowsByPredicate(_ => true);
                        viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.FolderFilterSelected);

                        var menu = (ContextMenu)window.FindResource("tableContextMenu");
                        menu.PlacementTarget = new FrameworkElement { DataContext = firstRow };
                        OpenContextMenu(menu);

                        Assert.AreEqual(1, availabilityCalls.Count);
                        Assert.AreSame(firstRow, availabilityCalls[0].Context);
                        Assert.AreEqual(2, availabilityCalls[0].Rows.Count);
                        Assert.AreSame(firstRow, availabilityCalls[0].Rows[0]);
                        Assert.AreSame(secondRow, availabilityCalls[0].Rows[1]);

                        MenuItem openUrl = FindMenuItem(menu, "tableContextMenuItemOpenURL");
                        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Import_Selected_Url, openUrl.Header);
                        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Import_Selected_Url_diff,
                            FindMenuItem(menu, "tableContextMenuItemOpenURLdiff").Header);
                        RoutedEventArgs openArgs = RaiseMenuClick(openUrl);
                        Assert.IsTrue(openArgs.Handled);
                        Assert.AreEqual(1, bulkCalls.Count);
                        Assert.IsFalse(bulkCalls[0].IsDiff);
                        Assert.AreEqual(2, bulkCalls[0].Rows.Count);
                        Assert.AreSame(firstRow, bulkCalls[0].Rows[0]);
                        Assert.AreSame(secondRow, bulkCalls[0].Rows[1]);
                        Assert.IsFalse(bulkCompletion.Task.IsCompleted);
                        Assert.AreEqual(0, expansionCalls);

                        bulkCompletion.SetResult(null);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(bulkCompletion.Task, "compiled-bulk-url-terminal");

                        RoutedEventArgs diffArgs = RaiseMenuClick(
                            FindMenuItem(menu, "tableContextMenuItemOpenURLdiff"));
                        Assert.IsTrue(diffArgs.Handled);
                        Assert.AreEqual(2, bulkCalls.Count);
                        Assert.IsTrue(bulkCalls[1].IsDiff);
                        Assert.AreEqual(2, bulkCalls[1].Rows.Count);
                        Assert.AreSame(firstRow, bulkCalls[1].Rows[0]);
                        Assert.AreSame(secondRow, bulkCalls[1].Rows[1]);

                        RoutedEventArgs lookupArgs = RaiseMenuClick(
                            FindMenuItem(menu, "tableContextMenuItemFindExternalPackage"));
                        Assert.IsTrue(lookupArgs.Handled);
                        Assert.AreEqual(1, externalLookupCalls.Count);
                        Assert.AreEqual(2, externalLookupCalls[0].Count);
                        Assert.AreSame(firstRow, externalLookupCalls[0][0]);
                        Assert.AreSame(secondRow, externalLookupCalls[0][1]);

                        // OpenedとClickの実接続を通し、画面固有の行範囲・メニュー名の切替を観測します。
                        foreach (string menuKey in new[] { "tableContextMenu", "tableContextMenuPlaylistMissing" })
                        {
                            var singleMenu = (ContextMenu)window.FindResource(menuKey);
                            table.SelectRowsByPredicate(row => ReferenceEquals(row, firstRow));
                            foreach (PlaylistDetailRow context in new[] { firstRow, secondRow })
                            {
                                singleMenu.PlacementTarget = new FrameworkElement { DataContext = context };
                                OpenContextMenu(singleMenu);
                                Assert.AreSame(context, availabilityCalls[^1].Context);
                                CollectionAssert.AreEqual(new object[] { context }, availabilityCalls[^1].Rows.ToArray());
                                foreach ((string name, bool isDiff, string header) in new[]
                                {
                                    ("tableContextMenuItemOpenURL", false, BeMusicSeeker.Properties.Resources.Open_Url),
                                    ("tableContextMenuItemOpenURLdiff", true, BeMusicSeeker.Properties.Resources.Open_Url_diff)
                                })
                                {
                                    MenuItem item = FindMenuItem(singleMenu, name);
                                    Assert.AreEqual(header, item.Header);
                                    int previousCalls = bulkCalls.Count;
                                    Assert.IsTrue(RaiseMenuClick(item).Handled);
                                    Assert.AreEqual(previousCalls + 1, bulkCalls.Count);
                                    Assert.AreEqual(isDiff, bulkCalls[^1].IsDiff);
                                    CollectionAssert.AreEqual(new object[] { context }, bulkCalls[^1].Rows.ToArray());
                                    Assert.AreEqual(0, singleUrls.Count, "単体右クリックをセルクリックの入口へ配送しません。");
                                }
                            }
                        }

                        table.SelectRowsByPredicate(_ => true);
                        var missingMenu = (ContextMenu)window.FindResource("tableContextMenuPlaylistMissing");
                        missingMenu.PlacementTarget = new FrameworkElement { DataContext = firstRow };
                        OpenContextMenu(missingMenu);
                        foreach ((string name, bool isDiff, string header) in new[]
                        {
                            ("tableContextMenuItemOpenURL", false, BeMusicSeeker.Properties.Resources.Import_Selected_Url),
                            ("tableContextMenuItemOpenURLdiff", true, BeMusicSeeker.Properties.Resources.Import_Selected_Url_diff)
                        })
                        {
                            MenuItem item = FindMenuItem(missingMenu, name);
                            Assert.AreEqual(header, item.Header);
                            int previousCalls = bulkCalls.Count;
                            Assert.IsTrue(RaiseMenuClick(item).Handled);
                            Assert.AreEqual(previousCalls + 1, bulkCalls.Count);
                            Assert.AreEqual(isDiff, bulkCalls[^1].IsDiff);
                            CollectionAssert.AreEqual(new object[] { firstRow, secondRow }, bulkCalls[^1].Rows.ToArray());
                        }

                        expansionEventSource.Publish();
                        Assert.AreEqual(1, expansionCalls);

                        table.ItemsSource = new List<object> { firstRow };
                        table.SelectRowsByPredicate(_ => true);
                        table.Width = 120d;
                        table.Height = 80d;
                        table.HeaderHeight = 0d;
                        table.RowHeight = 60d;
                        table.Columns =
                        [
                            new CustomTableColumn(
                                "Url1",
                                "URL1",
                                layout: null,
                                fallbackOrder: 0,
                                sortMemberPath: null,
                                alignment: TextAlignment.Center,
                                textSelector: _ => "download",
                                minWidth: 40,
                                maxWidth: 40,
                                canResize: false,
                                cellKind: CustomTableCellKind.DownloadIcon)
                        ];
                        MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
                        table.Measure(new Size(table.Width, table.Height));
                        table.Arrange(new Rect(0d, 0d, table.Width, table.Height));
                        table.UpdateLayout();

                        CustomTableHitTestResult hit = table.HitTestTable(new Point(20d, 30d));
                        Assert.AreEqual(CustomTableHitKind.Cell, hit.Kind);
                        Assert.AreEqual(0, hit.RowIndex);
                        Assert.AreSame(firstRow, hit.Row);
                        Assert.AreEqual("Url1", hit.Column?.Id);
                        Assert.AreEqual(CustomTableCellKind.DownloadIcon, hit.Column?.CellKind);

                        // 物理カーソルを使わず実hitからcompiled購読先へ渡すため、既存イベントの購読delegateだけを反射で呼びます。
                        FieldInfo actionField = typeof(CustomTableView).GetField(
                            nameof(CustomTableView.CellActionRequested),
                            BindingFlags.Instance | BindingFlags.NonPublic)!;
                        var actionSubscriber = actionField.GetValue(table) as Delegate;
                        Assert.IsNotNull(actionSubscriber);
                        actionSubscriber.DynamicInvoke(table, new CustomTableCellActionRequestedEventArgs(hit));
                        Assert.AreEqual(1, singleUrls.Count);
                        Assert.AreEqual(firstRow.Url, singleUrls[0]);
                        Assert.IsFalse(singleCompletion.Task.IsCompleted);
                        singleCompletion.SetResult(null);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(singleCompletion.Task, "compiled-single-url-terminal");
                    }
                    finally
                    {
                        singleCompletion.TrySetResult(null);
                        bulkCompletion.TrySetResult(null);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(singleCompletion.Task, "compiled-single-url-finally");
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(bulkCompletion.Task, "compiled-bulk-url-finally");
                    }
                },
                playlistWorkspaceTerminals: CreateTerminals(
                    urlAcquisition: urlAcquisition,
                    urlInstallTreeExpansionEventSource: expansionEventSource,
                    urlInstallTreeExpansion: expansion));

            Assert.AreEqual(0, expansionEventSource.SubscriberCount);
            expansionEventSource.Publish();
            Assert.AreEqual(1, expansionCalls);
        }
        finally
        {
            singleCompletion.TrySetResult(null);
            bulkCompletion.TrySetResult(null);
        }
    }

    /// <summary>所有側の導入完了イベントをcompiled画面が一度展開へ配送し、画面終了後のイベントでは展開しません。</summary>
    [TestMethod]
    public void PlaylistUrlInstallTreeExpansion_ForwardsOwnerEventAndUnsubscribes()
    {
        int expansionCalls = 0;
        PlaylistUrlInstallTreeExpansionEventSourceFake expansionEventSource = new();
        MainWindowPlaylistUrlInstallTreeExpansionTerminal expansion = new(() => expansionCalls++);

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (_, _) =>
            {
                Assert.AreEqual(1, expansionEventSource.SubscriberCount);

                Assert.AreEqual(0, expansionCalls);
                expansionEventSource.Publish();
                Assert.AreEqual(1, expansionCalls);
            },
            playlistWorkspaceTerminals: CreateTerminals(
                urlInstallTreeExpansionEventSource: expansionEventSource,
                urlInstallTreeExpansion: expansion));

        Assert.AreEqual(0, expansionEventSource.SubscriberCount);
        expansionEventSource.Publish();
        Assert.AreEqual(1, expansionCalls);
    }

    /// <summary>compiled表項目削除が元の選択二行を順序どおり終端へ渡し、非同期処理完了前にクリックを処理済みにします。</summary>
    [TestMethod]
    public void PlaylistEntryRemovalRoutesThroughWorkspaceOwner()
    {
        TaskCompletionSource<object?> completion = NewCompletion();
        IReadOnlyList<object>? capturedRows = null;
        int callCount = 0;
        BMSTable playlistTable = new() { name = "Playlist" };
        PlaylistDetailRow firstRow = CreatePlaylistRow(
            playlistTable,
            "11111111111111111111111111111111",
            @"C:\wave6e-playlist\first.bms");
        PlaylistDetailRow secondRow = CreatePlaylistRow(
            playlistTable,
            "22222222222222222222222222222222",
            @"C:\wave6e-playlist\second.bms");
        MainWindowPlaylistEntryRemovalTerminal entryRemoval = new(
            rows =>
            {
                callCount++;
                capturedRows = rows;
                return completion.Task;
            });

        try
        {
            MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
                MainWindowViewModelTestFactory.CreateIsolatedSettings(),
                (viewModel, window) =>
                {
                    try
                    {

                        var table = (CustomTableView)window.FindName("customTableView");
                        table.ItemsSource = new List<object> { firstRow, secondRow };
                        table.SelectRowsByPredicate(row => ReferenceEquals(row, firstRow) || ReferenceEquals(row, secondRow));
                        viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.FolderFilterSelected);

                        var menu = (ContextMenu)window.FindResource("tableContextMenu");
                        menu.PlacementTarget = new FrameworkElement { DataContext = firstRow };
                        MenuItem removeEntry = FindMenuItem(menu, "tableContextMenuItemDeleteEntry");
                        OpenContextMenu(menu);

                        RoutedEventArgs args = RaiseMenuClick(removeEntry);

                        Assert.IsTrue(args.Handled);
                        Assert.AreEqual(1, callCount);
                        Assert.IsFalse(completion.Task.IsCompleted);
                        Assert.IsNotNull(capturedRows);
                        Assert.AreEqual(2, capturedRows!.Count);
                        Assert.AreSame(firstRow, capturedRows![0]);
                        Assert.AreSame(secondRow, capturedRows![1]);

                    }
                    finally
                    {
                        completion.TrySetResult(null);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(completion.Task, "compiled-playlist-terminal-delegate");
                    }

                },
                playlistWorkspaceTerminals: CreateTerminals(entryRemoval: entryRemoval));
        }
        finally
        {
            completion.TrySetResult(null);
        }
    }

    /// <summary>compiledlevel上書きクリックが元表を一回だけ終端へ渡し、非同期処理完了前にイベントを処理済みにします。</summary>
    [TestMethod]
    public void PlaylistOverwriteLevel_RoutesThroughWorkflowOwner()
    {
        TaskCompletionSource<object?> completion = NewCompletion();
        BMSTable? capturedTable = null;
        int callCount = 0;
        BMSTable table = new() { name = "Overwrite target" };
        MainWindowPlaylistTableLevelOverwriteTerminal overwrite = new(
            captured =>
            {
                callCount++;
                capturedTable = captured;
                return completion.Task;
            });

        try
        {
            MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
                MainWindowViewModelTestFactory.CreateIsolatedSettings(),
                (_, window) =>
                {
                    try
                    {

                        var menu = (ContextMenu)window.FindResource("treeViewPlaylistTableContextMenu");
                        menu.PlacementTarget = new TreeViewItem { DataContext = table };
                        menu.DataContext = table;
                        OpenContextMenu(menu);

                        MenuItem overwriteLevel = FindMenuItem(
                            menu,
                            "treeViewPlaylistTableContextMenuItemOverwriteLevel");
                        overwriteLevel.DataContext = table;
                        RoutedEventArgs args = RaiseMenuClick(overwriteLevel);

                        Assert.IsTrue(args.Handled);
                        Assert.AreEqual(1, callCount);
                        Assert.AreSame(table, capturedTable);
                        Assert.IsFalse(completion.Task.IsCompleted);

                    }
                    finally
                    {
                        completion.TrySetResult(null);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(completion.Task, "compiled-playlist-terminal-delegate");
                    }

                },
                playlistWorkspaceTerminals: CreateTerminals(tableLevelOverwrite: overwrite));
        }
        finally
        {
            completion.TrySetResult(null);
        }
    }

    /// <summary>compiled表メニューでは外部同期中の新規folder作成だけを禁止し、再同期・ページ表示・上書き・削除の操作入口を利用可能に保ちます。</summary>
    [TestMethod]
    public void PlaylistTableContextMenu_PreservesActionsAndRejectsFolderCreationDuringExternalSync()
    {
        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (_, window) =>
            {
                var menu = (ContextMenu)window.FindResource("treeViewPlaylistTableContextMenu");
                foreach (BMSTable table in new[]
                {
                    new BMSTable { Page_url = new Uri("https://example.test/external"), is_external_sync = true },
                    new BMSTable { Page_url = new Uri("https://example.test/local"), is_external_sync = false }
                })
                {
                    menu.PlacementTarget = new TreeViewItem { DataContext = table };
                    menu.DataContext = table;
                    OpenContextMenu(menu);

                    Assert.IsTrue(FindMenuItem(menu, "treeViewPlaylistTableContextMenuItemReload").IsEnabled);
                    Assert.IsTrue(FindMenuItem(menu, "treeViewPlaylistTableContextMenuItemOpenPageURI").IsEnabled);
                    Assert.IsTrue(FindMenuItem(menu, "treeViewPlaylistTableContextMenuItemOverwriteLevel").IsEnabled);
                    Assert.AreEqual(
                        !table.is_external_sync,
                        FindMenuItem(menu, "treeViewPlaylistTableContextMenuItemCreateNewFolder").IsEnabled);
                    Assert.IsTrue(FindMenuItem(menu, "treeViewPlaylistTableContextMenuItemRemoveTable").IsEnabled);
                }

            });
    }

    /// <summary>全体lampからの実ナビゲーションはfolder選択を外して表rootを選び、その後に親のactivationとfocusを一度要求します。</summary>
    [TestMethod]
    public void PlaylistLampNavigation_SelectsTableRootThroughComposedMainWindowTerminal()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var foregroundOperations = new List<string>();
            ActualMainWindowFixture? fixture = null;
            fixture = CreateActualMainWindowFixture(
                windowTest,
                CreateForegroundTerminal(
                    foregroundOperations,
                    () =>
                    {
                        var playlistRoot = (TreeViewItem)fixture!.Window.FindName("treeViewItemPlaylist");
                        var selectedTable = playlistRoot.ItemContainerGenerator
                            .ContainerFromItem(fixture!.Table) as TreeViewItem;
                        Assert.IsNotNull(selectedTable);
                        Assert.IsTrue(
                            selectedTable!.IsSelected,
                            "the playlist root selection must be applied before foreground activation");
                    }));
            try
            {
                fixture.Table.entries =
                [
                    new BMSTableEntry
                    {
                        folder = "normal",
                        md5 = new string('a', 32)
                    }
                ];
                fixture.Table.Folder_order = ["normal"];
                TestUiDispatcherHost.ProcessQueuedPresentation();

                var playlistRoot = (TreeViewItem)fixture.Window.FindName("treeViewItemPlaylist");
                MaterializeTreeItems(playlistRoot);
                var tableItem = playlistRoot.ItemContainerGenerator
                    .ContainerFromItem(fixture.Table) as TreeViewItem;
                Assert.IsNotNull(tableItem, "the active playlist table must be materialized in the shell tree");
                tableItem!.IsExpanded = true;
                MaterializeTreeItems(tableItem);

                TreeViewItem? folderItem = FindDescendants<TreeViewItem>(tableItem)
                    .FirstOrDefault(item => item.DataContext is PlaylistFolderNode);
                Assert.IsNotNull(
                    folderItem,
                    "a playlist folder child must be materialized for the root-selection route");
                folderItem!.IsSelected = true;
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsTrue(folderItem!.IsSelected);

                var request = PlaylistLampViewerNavigationRequest.ForOverall(
                    fixture.Table.playlist_id!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    PlaylistLampSegmentKind.Clear,
                    PlaylistLampClearCategory.ASSIST,
                    rankCategory: null);
                Assert.IsTrue(fixture.ViewModel.PlaylistWorkspace.TryRequestPlaylistLampNavigation(request));
                TestUiDispatcherHost.ProcessQueuedPresentation();

                tableItem = playlistRoot.ItemContainerGenerator.ContainerFromItem(fixture.Table) as TreeViewItem;
                Assert.IsNotNull(tableItem);
                Assert.IsTrue(tableItem!.IsSelected, "overall lamp navigation must select the playlist table root");
                Assert.IsFalse(
                    FindDescendants<TreeViewItem>(tableItem!)
                        .Where(item => !ReferenceEquals(item, tableItem))
                        .Any(item => item.DataContext is PlaylistFolderNode && item.IsSelected),
                    "overall lamp navigation must not leave a child folder selected");
                CollectionAssert.AreEqual(
                    new[] { "activate", "focus" },
                    foregroundOperations,
                    "successful overall navigation must focus the main shell exactly once");
            }
            finally
            {
                fixture!.Close();
            }
        });
    }

    /// <summary>folder lampからの実ナビゲーションは対象folderを選び、その選択反映後に親のactivationとfocusを一度要求します。</summary>
    [TestMethod]
    public void PlaylistLampNavigation_SelectsFolderThroughComposedMainWindowTerminal()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var foregroundOperations = new List<string>();
            ActualMainWindowFixture? fixture = null;
            fixture = CreateActualMainWindowFixture(
                windowTest,
                CreateForegroundTerminal(
                    foregroundOperations,
                    () =>
                    {
                        var playlistRoot = (TreeViewItem)fixture!.Window.FindName("treeViewItemPlaylist");
                        var selectedTable = playlistRoot.ItemContainerGenerator
                            .ContainerFromItem(fixture!.Table) as TreeViewItem;
                        Assert.IsNotNull(selectedTable);
                        TreeViewItem? selectedFolder = FindDescendants<TreeViewItem>(selectedTable!)
                            .FirstOrDefault(item => item.DataContext is PlaylistFolderNode && item.IsSelected);
                        Assert.IsNotNull(
                            selectedFolder,
                            "the folder selection must be applied before foreground activation");
                        Assert.AreEqual(
                            "normal",
                            ((PlaylistFolderNode)selectedFolder!.DataContext).FolderName);
                    }));
            try
            {
                fixture.Table.entries =
                [
                    new BMSTableEntry
                    {
                        folder = "normal",
                        md5 = new string('a', 32)
                    }
                ];
                fixture.Table.Folder_order = ["normal"];
                TestUiDispatcherHost.ProcessQueuedPresentation();

                var playlistRoot = (TreeViewItem)fixture.Window.FindName("treeViewItemPlaylist");
                MaterializeTreeItems(playlistRoot);
                var tableItem = playlistRoot.ItemContainerGenerator
                    .ContainerFromItem(fixture.Table) as TreeViewItem;
                Assert.IsNotNull(tableItem, "the active playlist table must be materialized in the shell tree");
                tableItem!.IsExpanded = true;
                MaterializeTreeItems(tableItem);

                TreeViewItem? folderItem = FindDescendants<TreeViewItem>(tableItem)
                    .FirstOrDefault(item => item.DataContext is PlaylistFolderNode);
                Assert.IsNotNull(
                    folderItem,
                    "a playlist folder child must be materialized for the folder-selection route");
                tableItem!.IsSelected = true;
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsTrue(tableItem!.IsSelected);

                var request = PlaylistLampViewerNavigationRequest.ForFolder(
                    PlaylistLampSegmentInvocationRequest.ForClear(
                        fixture.Table.playlist_id!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "normal",
                        PlaylistLampClearCategory.ASSIST));
                Assert.IsTrue(fixture.ViewModel.PlaylistWorkspace.TryRequestPlaylistLampNavigation(request));
                TestUiDispatcherHost.ProcessQueuedPresentation();

                tableItem = playlistRoot.ItemContainerGenerator.ContainerFromItem(fixture.Table) as TreeViewItem;
                Assert.IsNotNull(tableItem);
                TreeViewItem? selectedFolder = FindDescendants<TreeViewItem>(tableItem!)
                    .FirstOrDefault(item => item.DataContext is PlaylistFolderNode && item.IsSelected);
                Assert.IsNotNull(selectedFolder, "folder lamp navigation must select the target folder");
                Assert.AreEqual("normal", ((PlaylistFolderNode)selectedFolder!.DataContext).FolderName);
                CollectionAssert.AreEqual(
                    new[] { "activate", "focus" },
                    foregroundOperations,
                    "successful folder navigation must focus the main shell exactly once");
            }
            finally
            {
                fixture!.Close();
            }
        });
    }

    /// <summary>最小化した親を復元した後にactivationとfocusを試み、activationがfalseでもfocus要求を省きません。</summary>
    [TestMethod]
    public void MainWindowForegroundTerminal_RestoresMinimizedThenAttemptsActivationAndFocus()
    {
        WindowState state = WindowState.Minimized;
        var operations = new List<string>();
        var terminal = new MainWindowForegroundTerminal(
            () => state,
            () =>
            {
                operations.Add("restore");
                state = WindowState.Normal;
            },
            () =>
            {
                operations.Add("activate");
                return false;
            },
            () =>
            {
                operations.Add("focus");
                return false;
            });

        terminal.FocusMainWindow();

        CollectionAssert.AreEqual(new[] { "restore", "activate", "focus" }, operations);
        Assert.AreEqual(WindowState.Normal, state);
    }

    /// <summary>compiled表削除は元表を一度配送し、拒否時に一覧を保ち、受理時に先に兄弟または空rootへ選択を移します。委譲先だけでなくfinallyの空root再要求を含む実イベント終端まで回収します。</summary>
    [TestMethod]
    public void PlaylistTableRemoval_RoutesThroughWorkflowOwner()
    {
        RunPlaylistTableRemovalRejectionScenario();
        RunPlaylistTableRemovalSiblingFallbackScenario();
        RunPlaylistTableRemovalEmptyRootScenario();
    }

    /// <summary>表削除の実メニューから元表を一度だけ渡し、Task失敗を一般通知で伝え、空rootの選択と詳細対象なしを維持します。</summary>
    [TestMethod]
    public void PlaylistTableRemovalFailure_NotifiesAndPreservesEmptyRootSelection()
    {
        RunPlaylistTableRemovalFailureScenario(
            UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
    }

    /// <summary>compiled表削除の失敗通知も成立しない場合に処理を再試行せず、一回の通知と空rootの選択を維持します。</summary>
    [TestMethod]
    public void PlaylistTableRemovalNotificationFailure_IsDiagnosticOnlyAndDoesNotRetry()
    {
        RunPlaylistTableRemovalFailureScenario(
            UiDialogResult.Failed(new InvalidOperationException("playlist removal notification failed")));
    }

    /// <summary>確認dialogで取消した表削除は実操作や失敗通知を開始せず、元一覧とroot選択を保ちます。</summary>
    [TestMethod]
    public void PlaylistTableRemovalCancellation_DoesNotNotify()
    {
        // これは確認 dialog の Cancel であり、operation が OperationCanceledException になる経路とは分けて観測する。
        var dialogs = new RecordingPlaylistWorkspaceDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
        };

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (_, window) =>
            {
                (TreeViewItem playlistRoot, MenuItem removeTable) =
                    PreparePlaylistTableRemovalContext(window, new BMSTable { name = "Cancelled" });

                RoutedEventArgs args = RaiseMenuClick(removeTable);
                Assert.IsTrue(args.Handled);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    dialogs.ConfirmationShown.Task,
                    "playlist table removal cancellation confirmation");
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.AreEqual(1, dialogs.ConfirmationRequests.Count);
                Assert.AreEqual(0, dialogs.MessageRequests.Count);
                Assert.IsTrue(playlistRoot.IsSelected);
                Assert.AreEqual(1, playlistRoot.Items.Count);
            },
            playlistWorkspaceDialogService: dialogs);
    }

    /// <summary>受理後の削除Taskが取消になっても失敗通知を出さず、先に移した空root選択と詳細対象なしを維持します。</summary>
    [TestMethod]
    public void PlaylistTableRemovalOperationCancellation_DoesNotNotify()
    {
        TaskCompletionSource<object?> selectionCompletion = NewCompletion();
        var dialogs = new RecordingPlaylistWorkspaceDialogService();
        var cancellationToken = new CancellationToken(canceled: true);
        int callCount = 0;
        MainWindowPlaylistTableRemovalTerminal removal = new(
            (_, applySelectionBeforeMutation) =>
            {
                callCount++;
                applySelectionBeforeMutation();
                return Task.FromCanceled(cancellationToken);
            });

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (viewModel, window) =>
            {
                EventHandler<PlaylistTreeSelectionActivatedEventArgs> selectionActivated = (_, args) =>
                {
                    if (!args.IsSummary && args.Detail != null && args.Detail.Table == null)
                    {
                        selectionCompletion.TrySetResult(null);
                    }
                };
                viewModel.PlaylistWorkspace.TreeSelectionActivated += selectionActivated;
                try
                {
                    (TreeViewItem playlistRoot, MenuItem removeTable) =
                        PreparePlaylistTableRemovalContext(window, new BMSTable { name = "Cancelled operation" });
                    playlistRoot.Items.Clear();
                    playlistRoot.IsSelected = true;
                    TestUiDispatcherHost.ProcessQueuedPresentation();

                    RoutedEventArgs args = RaiseMenuClick(removeTable);
                    Assert.IsTrue(args.Handled);
                    Assert.AreEqual(1, callCount);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        selectionCompletion.Task,
                        "playlist table removal operation cancellation cleanup");
                    TestUiDispatcherHost.ProcessQueuedPresentation();

                    Assert.AreEqual(0, dialogs.MessageRequests.Count);
                    Assert.IsTrue(playlistRoot.IsSelected);
                    Assert.AreEqual(0, playlistRoot.Items.Count);
                    Assert.IsNull(viewModel.PlaylistWorkspace.CapturePlaylistDetailSelection().Table);
                }
                finally
                {
                    viewModel.PlaylistWorkspace.TreeSelectionActivated -= selectionActivated;
                }
            },
            playlistWorkspaceTerminals: CreateTerminals(tableRemoval: removal),
            playlistWorkspaceDialogService: dialogs);
    }

    private static void RunPlaylistTableRemovalFailureScenario(UiDialogResult messageResult)
    {
        BMSTable firstTable = new() { name = "First" };
        TaskCompletionSource<object?> selectionCompletion = NewCompletion();
        var failure = new InvalidOperationException("playlist table removal persistence failed");
        var dialogs = new RecordingPlaylistWorkspaceDialogService
        {
            MessageResult = messageResult
        };
        int callCount = 0;
        BMSTable? capturedTable = null;
        PlaylistTreeSelectionActivatedEventArgs? emptyRootSelection = null;
        MainWindowPlaylistTableRemovalTerminal removal = new(
            (table, applySelectionBeforeMutation) =>
            {
                callCount++;
                capturedTable = table;
                applySelectionBeforeMutation();
                return Task.FromException(failure);
            });

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (viewModel, window) =>
            {
                EventHandler<PlaylistTreeSelectionActivatedEventArgs> selectionActivated = (_, args) =>
                {
                    if (!args.IsSummary && args.Detail != null && args.Detail.Table == null)
                    {
                        emptyRootSelection = args;
                        selectionCompletion.TrySetResult(null);
                    }
                };
                viewModel.PlaylistWorkspace.TreeSelectionActivated += selectionActivated;
                try
                {
                    (TreeViewItem playlistRoot, MenuItem removeTable) =
                        PreparePlaylistTableRemovalContext(window, firstTable);
                    playlistRoot.Items.Clear();
                    playlistRoot.IsSelected = true;
                    TestUiDispatcherHost.ProcessQueuedPresentation();

                    RoutedEventArgs? args = null;
                    Task terminal = MainWindowPresentationTestHarness.CaptureAsyncEventCompletion(
                        () => args = RaiseMenuClick(removeTable));
                    // 通知開始だけでなく実イベント終端と空rootの選択通知まで待ちます。
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        Task.WhenAll(terminal, selectionCompletion.Task, dialogs.MessageShown.Task),
                        "playlist table removal failure notification and empty-root cleanup");
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.IsNotNull(args);
                    Assert.IsTrue(args.Handled);
                    Assert.AreEqual(1, callCount);
                    Assert.AreSame(firstTable, capturedTable);

                    Assert.IsNotNull(emptyRootSelection);
                    Assert.IsFalse(emptyRootSelection!.IsSummary);
                    Assert.IsNotNull(emptyRootSelection!.Detail);
                    Assert.IsNull(emptyRootSelection!.Detail.Table);
                    Assert.AreEqual(1, dialogs.MessageRequests.Count);
                    Assert.AreEqual(Resources.Msg_error_unexpected, dialogs.MessageRequests[0].MessageBoxText);
                    Assert.AreSame(window, dialogs.MessageRequests[0].Owner);
                    Assert.AreEqual(MessageBoxImage.Hand, dialogs.MessageRequests[0].Icon);
                    Assert.IsTrue(playlistRoot.IsSelected);
                    Assert.AreEqual(0, playlistRoot.Items.Count);
                    Assert.IsNull(viewModel.PlaylistWorkspace.CapturePlaylistDetailSelection().Table);
                }
                finally
                {
                    viewModel.PlaylistWorkspace.TreeSelectionActivated -= selectionActivated;
                }
            },
            playlistWorkspaceTerminals: CreateTerminals(tableRemoval: removal),
            playlistWorkspaceDialogService: dialogs);
    }

    private static void RunPlaylistTableRemovalRejectionScenario()
    {
        BMSTable firstTable = new() { name = "First" };
        BMSTable secondTable = new() { name = "Second" };
        TaskCompletionSource<object?> completion = NewCompletion();
        int callCount = 0;
        BMSTable? capturedTable = null;
        MainWindowPlaylistTableRemovalTerminal removal = new(
            (table, _) =>
            {
                callCount++;
                capturedTable = table;
                return completion.Task;
            });

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (_, window) =>
            {
                Task handlerCompletion = Task.CompletedTask;
                try
                {
                    (TreeViewItem playlistRoot, MenuItem removeTable) =
                        PreparePlaylistTableRemovalContext(window, firstTable, secondTable);

                    RoutedEventArgs? args = null;
                    handlerCompletion = MainWindowPresentationTestHarness.CaptureAsyncEventCompletion(
                        () => args = RaiseMenuClick(removeTable));
                    Assert.IsNotNull(args);
                    Assert.IsTrue(args.Handled);
                    Assert.AreEqual(1, callCount);
                    Assert.AreSame(firstTable, capturedTable);
                    Assert.IsTrue(playlistRoot.IsSelected);
                    Assert.IsFalse(completion.Task.IsCompleted);

                    QueueCompletion(completion);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        handlerCompletion,
                        "playlist table removal rejection");
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.IsTrue(playlistRoot.IsSelected);
                    Assert.AreEqual(2, playlistRoot.Items.Count);
                }
                finally
                {
                    completion.TrySetResult(null);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(handlerCompletion, "playlist-table-removal-rejection-event-cleanup");
                }
            },
            playlistWorkspaceTerminals: CreateTerminals(tableRemoval: removal));
    }

    private static void RunPlaylistTableRemovalSiblingFallbackScenario()
    {
        BMSTable firstTable = new() { name = "First" };
        BMSTable secondTable = new() { name = "Second" };
        TaskCompletionSource<object?> completion = NewCompletion();
        TaskCompletionSource<object?> selectionCompletion = NewCompletion();
        int callbackInvocationCount = 0;
        var callbacks = new List<System.Action>();
        var capturedTables = new List<BMSTable>();
        MainWindowPlaylistTableRemovalTerminal removal = new(
            (table, applySelectionBeforeMutation) =>
            {
                capturedTables.Add(table);
                callbacks.Add(applySelectionBeforeMutation);
                callbackInvocationCount++;
                applySelectionBeforeMutation();
                return completion.Task;
            });

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (viewModel, window) =>
            {
                EventHandler<PlaylistTreeSelectionActivatedEventArgs> selectionActivated = (_, args) =>
                {
                    if (!args.IsSummary && args.Detail != null && ReferenceEquals(args.Detail.Table, secondTable))
                    {
                        selectionCompletion.TrySetResult(null);
                    }
                };
                viewModel.PlaylistWorkspace.TreeSelectionActivated += selectionActivated;
                Task handlerCompletion = Task.CompletedTask;
                try
                {
                    (TreeViewItem playlistRoot, MenuItem removeTable) =
                        PreparePlaylistTableRemovalContext(window, firstTable, secondTable);

                    RoutedEventArgs? args = null;
                    handlerCompletion = MainWindowPresentationTestHarness.CaptureAsyncEventCompletion(
                        () => args = RaiseMenuClick(removeTable));
                    Assert.IsNotNull(args);
                    Assert.IsTrue(args.Handled);
                    Assert.AreEqual(1, capturedTables.Count);
                    Assert.AreSame(firstTable, capturedTables[0]);
                    Assert.AreEqual(1, callbacks.Count);
                    Assert.AreEqual(1, callbackInvocationCount);
                    Assert.IsFalse(completion.Task.IsCompleted);

                    QueueCompletion(completion);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        Task.WhenAll(handlerCompletion, selectionCompletion.Task),
                        "playlist table removal sibling fallback");
                    TestUiDispatcherHost.ProcessQueuedPresentation();

                    var sibling = playlistRoot.ItemContainerGenerator.ContainerFromItem(secondTable) as TreeViewItem;
                    Assert.IsNotNull(sibling);
                    Assert.IsTrue(sibling!.IsSelected);
                    Assert.AreSame(secondTable, sibling!.DataContext);
                }
                finally
                {
                    completion.TrySetResult(null);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(handlerCompletion, "playlist-table-removal-sibling-event-cleanup");
                    viewModel.PlaylistWorkspace.TreeSelectionActivated -= selectionActivated;
                }
            },
            playlistWorkspaceTerminals: CreateTerminals(tableRemoval: removal));
    }

    private static void RunPlaylistTableRemovalEmptyRootScenario()
    {
        BMSTable firstTable = new() { name = "First" };
        TaskCompletionSource<object?> completion = NewCompletion();
        TaskCompletionSource<object?> selectionCompletion = NewCompletion();
        PlaylistTreeSelectionActivatedEventArgs? emptyRootSelection = null;
        int callbackInvocationCount = 0;
        var callbacks = new List<System.Action>();
        var capturedTables = new List<BMSTable>();
        MainWindowPlaylistTableRemovalTerminal removal = new(
            (table, applySelectionBeforeMutation) =>
            {
                capturedTables.Add(table);
                callbacks.Add(applySelectionBeforeMutation);
                callbackInvocationCount++;
                applySelectionBeforeMutation();
                return completion.Task;
            });

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (viewModel, window) =>
            {
                EventHandler<PlaylistTreeSelectionActivatedEventArgs> selectionActivated = (_, args) =>
                {
                    if (!args.IsSummary && args.Detail != null && args.Detail.Table == null)
                    {
                        emptyRootSelection = args;
                        selectionCompletion.TrySetResult(null);
                    }
                };
                viewModel.PlaylistWorkspace.TreeSelectionActivated += selectionActivated;
                Task handlerCompletion = Task.CompletedTask;
                try
                {
                    (TreeViewItem playlistRoot, MenuItem removeTable) =
                        PreparePlaylistTableRemovalContext(window, firstTable);
                    playlistRoot.Items.Clear();
                    playlistRoot.IsSelected = true;
                    TestUiDispatcherHost.ProcessQueuedPresentation();

                    RoutedEventArgs? args = null;
                    handlerCompletion = MainWindowPresentationTestHarness.CaptureAsyncEventCompletion(
                        () => args = RaiseMenuClick(removeTable));
                    Assert.IsNotNull(args);
                    Assert.IsTrue(args.Handled);
                    Assert.AreEqual(1, capturedTables.Count);
                    Assert.AreSame(firstTable, capturedTables[0]);
                    Assert.AreEqual(1, callbacks.Count);
                    Assert.AreEqual(1, callbackInvocationCount);
                    Assert.IsFalse(completion.Task.IsCompleted);

                    QueueCompletion(completion);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        Task.WhenAll(handlerCompletion, selectionCompletion.Task),
                        "playlist table removal empty-root detail selection");
                    TestUiDispatcherHost.ProcessQueuedPresentation();

                    Assert.IsNotNull(emptyRootSelection);
                    Assert.IsFalse(emptyRootSelection!.IsSummary);
                    Assert.IsNotNull(emptyRootSelection!.Detail);
                    Assert.IsNull(emptyRootSelection!.Detail.Table);
                    Assert.IsTrue(playlistRoot.IsSelected);
                    Assert.AreEqual(0, playlistRoot.Items.Count);
                    PlaylistDetailSelection emptyDetailSelection =
                        viewModel.PlaylistWorkspace.CapturePlaylistDetailSelection();
                    Assert.IsNotNull(emptyDetailSelection, "実削除イベントの終端後も空rootの詳細選択を保持します。");
                    Assert.IsNull(emptyDetailSelection.Table);
                }
                finally
                {
                    completion.TrySetResult(null);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(handlerCompletion, "playlist-table-removal-empty-root-event-cleanup");
                    viewModel.PlaylistWorkspace.TreeSelectionActivated -= selectionActivated;
                }
            },
            playlistWorkspaceTerminals: CreateTerminals(tableRemoval: removal));
    }

    private static (TreeViewItem PlaylistRoot, MenuItem RemoveTable) PreparePlaylistTableRemovalContext(
        MainWindow window,
        params BMSTable[] tables)
    {
        var playlistRoot = (TreeViewItem)window.FindName("treeViewItemPlaylist");
        playlistRoot.ItemsSource = null;
        playlistRoot.Items.Clear();
        foreach (BMSTable table in tables)
        {
            playlistRoot.Items.Add(table);
        }
        MaterializeTreeItems(playlistRoot);
        playlistRoot.IsSelected = true;
        TestUiDispatcherHost.ProcessQueuedPresentation();

        var menu = (ContextMenu)window.FindResource("treeViewPlaylistTableContextMenu");
        BMSTable firstTable = tables[0];
        menu.PlacementTarget = new TreeViewItem { DataContext = firstTable };
        menu.DataContext = firstTable;
        OpenContextMenu(menu);
        MenuItem removeTable = FindMenuItem(
            menu,
            "treeViewPlaylistTableContextMenuItemRemoveTable");
        removeTable.DataContext = firstTable;
        return (playlistRoot, removeTable);
    }

    private static void QueueCompletion(TaskCompletionSource<object?> completion)
    {
        TestUiDispatcherHost.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Normal,
            new Action(() => completion.TrySetResult(null)));
    }

    private static MainWindowPlaylistWorkspaceTerminals CreateTerminals(
        MainWindowPlaylistEntryRemovalTerminal? entryRemoval = null,
        MainWindowPlaylistTableLevelOverwriteTerminal? tableLevelOverwrite = null,
        MainWindowPlaylistTableRemovalTerminal? tableRemoval = null,
        MainWindowPlaylistUrlAcquisitionTerminal? urlAcquisition = null,
        IMainWindowPlaylistUrlInstallTreeExpansionEventSource? urlInstallTreeExpansionEventSource = null,
        MainWindowPlaylistUrlInstallTreeExpansionTerminal? urlInstallTreeExpansion = null,
        MainWindowPlaylistTablesReloadTerminal? tablesReload = null)
    {
        return new MainWindowPlaylistWorkspaceTerminals(
            entryRemoval ?? new MainWindowPlaylistEntryRemovalTerminal(_ => Task.CompletedTask),
            tableLevelOverwrite ?? new MainWindowPlaylistTableLevelOverwriteTerminal(_ => Task.CompletedTask),
            tableRemoval ?? new MainWindowPlaylistTableRemovalTerminal((_, _) => Task.CompletedTask),
            urlAcquisition ?? new MainWindowPlaylistUrlAcquisitionTerminal(
                (_) => Task.CompletedTask,
                (_, _) => Task.CompletedTask,
                (_) => Task.CompletedTask,
                (_, _) => new PlaylistUrlContextMenuAvailability(
                    isPlaylistContext: false,
                    isBulkContext: false,
                    canOpenUrl: false,
                    canOpenDiffUrl: false,
                    canFindExternalPackage: false)),
            urlInstallTreeExpansionEventSource ?? new PlaylistUrlInstallTreeExpansionEventSourceFake(),
            urlInstallTreeExpansion ?? new MainWindowPlaylistUrlInstallTreeExpansionTerminal(() => { }),
            tablesReload ?? new MainWindowPlaylistTablesReloadTerminal(() => Task.CompletedTask));
    }

    private sealed class PlaylistUrlInstallTreeExpansionEventSourceFake
        : IMainWindowPlaylistUrlInstallTreeExpansionEventSource
    {
        private Action? handlers;

        internal int SubscriberCount => handlers?.GetInvocationList().Length ?? 0;

        public void Subscribe(Action handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            handlers += handler;
        }

        public void Unsubscribe(Action handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            handlers -= handler;
        }

        internal void Publish()
        {
            handlers?.Invoke();
        }
    }

    private static PlaylistDetailRow CreatePlaylistRow(BMSTable table, string md5, string path)
    {
        ChartFile file = ChartTestValues.Empty(ChartFileKind.Bmson) with { Md5 = md5, Path = path, RawTitle = md5 };
        BMSTableEntry entry = new() { md5 = md5, parent = table };
        return new PlaylistDetailSourceRow(
            entry,
            (file))
            .CreateViewRow();
    }

    private static PlaylistDetailRow CreatePlaylistUrlRow(
        BMSTable table,
        string md5,
        string url,
        string urlDiff)
    {
        ChartFile file = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Md5 = md5,
            Path = $@"C:\wave6e-playlist-url\{md5}.bms",
            RawTitle = md5
        };
        BMSTableEntry entry = new()
        {
            md5 = md5,
            parent = table,
            Url = new Uri(url),
            Url_diff = new Uri(urlDiff)
        };
        return new PlaylistDetailSourceRow(
            entry,
            (file))
            .CreateViewRow();
    }

    private static TaskCompletionSource<object?> NewCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewCompletion<T>()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static BMSTable CreateAdmissionFixtureTable()
    {
        var entry = new BMSTableEntry
        {
            md5 = "cccccccccccccccccccccccccccccccc",
            title = "Local admission entry",
            folder = "Local"
        };
        return new BMSTable
        {
            playlist_id = 2,
            name = "Local admission table",
            symbol = "LOCAL",
            entry_type = LR2SongDBExtended.playlist.EntryUnitType.File,
            is_bmt_output = false,
            ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.None,
            Folder_order = ["Local"],
            entries = [entry]
        };
    }

    private static void PersistFixtureTable(string songDbPath, BMSTable table)
    {
        using var database = new LR2SongDBExtended(songDbPath);
        database.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
        foreach (BMSTableEntry entry in table.entries ?? [])
        {
            database.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
        }
        database.Commit();
    }

    private sealed class BlockingPlaylistJsonServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new();
        private readonly object responseSync = new();
        private readonly List<ResponseStage> responseStages = [];
        private readonly Task serverTask;
        private TcpClient? acceptedClient;
        private ResponseStage? currentResponseStage;
        private int nextResponseStageIndex;
        private int disposed;

        internal BlockingPlaylistJsonServer(string headerJson, string scoreJson)
        {
            EnqueueResponse(headerJson, scoreJson);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            PageUri = new Uri("http://127.0.0.1:" + port + "/playlist.json");
            serverTask = ServeAsync();
        }

        internal Uri PageUri { get; }

        /// <summary>HTTP到達待機中にも実serverの早期失敗・終端を観測し、通知だけの待機を残さないためのTaskです。</summary>
        internal Task Completion => serverTask;

        internal TaskCompletionSource<object?> DataRequestAccepted => responseStages[0].DataRequestAccepted;

        internal void ReleaseResponse()
        {
            responseStages[0].ReleaseResponse();
        }

        internal ResponseStage EnqueueResponse(string headerJson, string scoreJson)
        {
            var response = new ResponseStage(headerJson, scoreJson);
            lock (responseSync)
            {
                responseStages.Add(response);
            }
            return response;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }
            ReleaseResponse();
            lock (responseSync)
            {
                foreach (ResponseStage response in responseStages)
                {
                    response.ReleaseResponse();
                }
            }
            lifetime.Cancel();
            listener.Stop();
            acceptedClient?.Dispose();
            try
            {
                serverTask.Wait();
            }
            catch (AggregateException exception) when (exception.InnerExceptions.All(IsExpectedShutdownException))
            {
            }
            finally
            {
                lifetime.Dispose();
            }
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    using TcpClient client = await listener.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false);
                    acceptedClient = client;
                    using NetworkStream stream = client.GetStream();
                    string requestTarget = await ReadRequestTargetAsync(stream, lifetime.Token).ConfigureAwait(false);
                    bool isDataRequest = requestTarget.EndsWith("/score.json", StringComparison.OrdinalIgnoreCase);
                    ResponseStage response = GetResponseStage(isDataRequest);
                    byte[] body = Encoding.UTF8.GetBytes(isDataRequest ? response.ScoreJson : response.HeaderJson);
                    byte[] header = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\n"
                        + "Content-Type: application/json; charset=utf-8\r\n"
                        + "Content-Length: " + body.Length + "\r\n"
                        + "Connection: close\r\n\r\n");
                    await stream.WriteAsync(header, lifetime.Token).ConfigureAwait(false);
                    await stream.FlushAsync(lifetime.Token).ConfigureAwait(false);
                    if (isDataRequest)
                    {
                        response.DataRequestAccepted.TrySetResult(null);
                        await response.ResponseReleased.Task.WaitAsync(lifetime.Token).ConfigureAwait(false);
                        await stream.WriteAsync(body, lifetime.Token).ConfigureAwait(false);
                        await stream.FlushAsync(lifetime.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        await stream.WriteAsync(body, lifetime.Token).ConfigureAwait(false);
                        await stream.FlushAsync(lifetime.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (SocketException) when (lifetime.IsCancellationRequested)
            {
            }
        }

        private ResponseStage GetResponseStage(bool isDataRequest)
        {
            lock (responseSync)
            {
                currentResponseStage ??= nextResponseStageIndex < responseStages.Count
                    ? responseStages[nextResponseStageIndex]
                    : throw new InvalidOperationException("No playlist response stage was queued.");
                ResponseStage response = currentResponseStage;
                if (isDataRequest)
                {
                    currentResponseStage = null;
                    nextResponseStageIndex++;
                }
                return response;
            }
        }

        internal sealed class ResponseStage
        {
            internal ResponseStage(string headerJson, string scoreJson)
            {
                HeaderJson = headerJson ?? throw new ArgumentNullException(nameof(headerJson));
                ScoreJson = scoreJson ?? throw new ArgumentNullException(nameof(scoreJson));
            }

            internal string HeaderJson { get; }

            internal string ScoreJson { get; }

            internal TaskCompletionSource<object?> DataRequestAccepted { get; } = NewCompletion();

            internal TaskCompletionSource<object?> ResponseReleased { get; } = NewCompletion();

            internal void ReleaseResponse()
            {
                ResponseReleased.TrySetResult(null);
            }
        }

        private static async Task<string> ReadRequestTargetAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[4096];
            int length = 0;
            while (true)
            {
                int read = await stream.ReadAsync(
                    buffer.AsMemory(length, buffer.Length - length),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("The playlist HTTP client closed before request headers completed.");
                }
                length += read;
                int terminator = FindHeaderTerminator(buffer, length);
                if (terminator < 0)
                {
                    if (length == buffer.Length)
                    {
                        throw new InvalidDataException("The playlist HTTP request headers exceeded the test limit.");
                    }
                    continue;
                }
                string requestLine = Encoding.ASCII.GetString(buffer, 0, terminator).Split(" ", StringSplitOptions.RemoveEmptyEntries)[1];
                return requestLine;
            }
        }

        private static int FindHeaderTerminator(byte[] buffer, int length)
        {
            for (int index = 3; index < length; index++)
            {
                if (buffer[index - 3] == '\r'
                    && buffer[index - 2] == '\n'
                    && buffer[index - 1] == '\r'
                    && buffer[index] == '\n')
                {
                    return index - 3;
                }
            }
            return -1;
        }

        private static bool IsExpectedShutdownException(Exception exception)
        {
            return exception is OperationCanceledException
                or ObjectDisposedException
                or SocketException;
        }
    }

    private static void ReassertNonActivatingPosition(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        // source初期化のnative配置復元は表示scopeの準備後に動くため、Loaded後にも画面外位置を一回設定します。
        // logical/physical pixelの差を含めて十分離し、実MainWindow経路でも前面操作を避けます。
        window.Left = SystemParameters.VirtualScreenLeft
            + (SystemParameters.VirtualScreenWidth * 4d)
            + 4096d;
        window.Top = SystemParameters.VirtualScreenTop
            + (SystemParameters.VirtualScreenHeight * 4d)
            + 4096d;
    }

    private static UiDialogCoordinator CreateActualRouteDialogService(
        TestWindowPresentationScope windowTest,
        ModalPreparationRecorder modalPreparation,
        Func<bool>? closeNextMessage = null,
        Func<Window, UiMessageRequest, ThemedMessageBoxResponse>? messagePresenter = null)
    {
        ArgumentNullException.ThrowIfNull(windowTest);
        ArgumentNullException.ThrowIfNull(modalPreparation);
        Func<Window, IDisposable> modalScope = dialogWindow =>
            {
                // 実coordinatorの所有解決後、ShowDialogの直前に共通scopeの非活性表示準備を接続します。
                windowTest.PrepareForOwnedPresentation(dialogWindow);
                modalPreparation.Record(dialogWindow);
                RoutedEventHandler? closeOnLoaded = null;
                DispatcherOperation? closeOperation = null;
                if (closeNextMessage?.Invoke() == true)
                {
                    closeOnLoaded = (_, _) =>
                    {
                        dialogWindow.Loaded -= closeOnLoaded!;
                        closeOperation = dialogWindow.Dispatcher.BeginInvoke(
                            DispatcherPriority.Loaded,
                            new Action(() =>
                            {
                                closeOperation = null;
                                if (dialogWindow.IsVisible)
                                {
                                    dialogWindow.Close();
                                }
                            }));
                    };
                    dialogWindow.Loaded += closeOnLoaded!;
                }

                IDisposable activeModalScope = UiDialogOwnerResolver.PushActiveModal(dialogWindow);
                return new CallbackDisposable(() =>
                {
                    if (closeOperation?.Status == DispatcherOperationStatus.Pending)
                    {
                        closeOperation.Abort();
                    }
                    if (closeOnLoaded != null)
                    {
                        dialogWindow.Loaded -= closeOnLoaded!;
                    }
                    activeModalScope.Dispose();
                });
            };
        return messagePresenter == null
            ? new UiDialogCoordinator(new UiDialogOwnerResolver(), modalScope)
            : new UiDialogCoordinator(new UiDialogOwnerResolver(), modalScope, messagePresenter);
    }

    private static MainWindowForegroundTerminal CreateForegroundTerminal(
        ICollection<string> operations,
        Action? activationObserved = null)
    {
        ArgumentNullException.ThrowIfNull(operations);
        return new MainWindowForegroundTerminal(
            () => WindowState.Normal,
            () => operations.Add("restore"),
            () =>
            {
                activationObserved?.Invoke();
                operations.Add("activate");
                return true;
            },
            () =>
            {
                operations.Add("focus");
                return true;
            });
    }

    private static ActualMainWindowFixture CreateActualMainWindowFixture(
        TestWindowPresentationScope windowTest,
        MainWindowForegroundTerminal? foregroundTerminal = null,
        Func<bool>? closeNextMessage = null,
        Func<CancellationToken, Task<WalkureScoreInput>>? recommendationScoreReader = null,
        Func<Window, UiMessageRequest, ThemedMessageBoxResponse>? messagePresenter = null,
        Action<Settings>? configureSettings = null,
        Func<BeatorajaBmtOptionsSnapshot>? bmtOptionsProvider = null,
        bool withScoreDatabase = false)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            nameof(MainWindowPlaylistWorkspaceWpfTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        // 実起動の事前読取と固定factoryが同じ専用の単独動作DBを使う。
        var applicationPath = ApplicationPathSnapshot.FromExecutablePath(Path.Combine(root, "BeMusicSeeker.exe"));
        Directory.CreateDirectory(applicationPath.DataDirectoryPath);
        string songDbPath = applicationPath.StandaloneSongDbPath;
        StartupLibraryConstructionTestSupport.CreateSongDatabase(songDbPath);
        PlaylistPersistenceRepository.EnsureSchema(songDbPath);
        string? scoreDbPath = withScoreDatabase ? Path.Combine(applicationPath.DataDirectoryPath, "score.db") : null;
        if (scoreDbPath != null)
        {
            using var scoreDb = new SQLite.SQLiteConnection(scoreDbPath);
            scoreDb.CreateTable<LR2ScoreDB.player>();
            scoreDb.CreateTable<LR2ScoreDB.score>();
        }

        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.BMSRootPath = root;
                values.StandaloneBmsRootPaths = root;
                values.BMSInstallDir = root;
                values.TableListURL = new Uri("http://127.0.0.1:1/table-list.json");
                values.EnablePlaylistUrlCompletion = false;
                values.ScanBmsFilesOnStartup = false;
                values.SkipInitPlaylistLoad = true;
                values.UseBeatorajaScoreDb = false;
                values.EnableBeatorajaBmtOutput = false;
                values.UseExternalPanelImage = false;
                values.UsePlayeruBMplay = false;
                values.UsePlayerLR2body = false;
                values.UsePlayerBMIIDXView = false;
                values.IsLR2BackupEnabled = false;
                values.RightClickActionsJson = RightClickActionSettingsDefaults.SerializedJson;
            });
        configureSettings?.Invoke(settings);
        var lifetime = new RecordingApplicationLifetime();
        var modalPreparation = new ModalPreparationRecorder();
        UiDialogCoordinator actualRouteDialogService = CreateActualRouteDialogService(
            windowTest,
            modalPreparation,
            closeNextMessage, messagePresenter);
        var composition = new ApplicationComposition(
            settingsEditSession: new NoOpSettingsEditSession(settings),
            uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            applicationLifetime: lifetime,
            applicationPathSnapshot: applicationPath,
            cultureCatalog: TestApplicationContext.CreateCultureCatalog());
        Lr2ConfigOwner? lr2ConfigOwner = settings.OperationModeLR2DB ? composition.ResolveLr2Config(settings.LR2ConfigXmlPath) : null;
        var library = new TestBmsLibrary(
            songDbPath,
            getLR2Config: lr2ConfigOwner,
            _lr2ScoreDB: scoreDbPath,
            startupRequiredFileScanReason: null,
            optionsSnapshotProvider: () => BmsLibraryOptionsSnapshot.CreateCurrent(settings),
            operationAdmission: composition.OperationAdmission,
            playlistOperationAdmission: composition.PlaylistOperationAdmission);
        var playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), songDbPath,
            () => CustomFolderOutputSettingsSnapshot.CreateCurrent(settings),
            getLr2Config: lr2ConfigOwner,
            beatorajaBmtOptionsProvider: bmtOptionsProvider,
            recommendationScoreReader: recommendationScoreReader, settings: settings);
        var table = new BMSTable
        {
            playlist_id = 1,
            bmt_sort = 1,
            name = NativeModalFixturePlaylistName,
            entry_type = LR2SongDBExtended.playlist.EntryUnitType.File,
            is_bmt_output = false,
            ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.None
        };
        playlist.BMSTables = new ObservableCollection<BMSTable> { table };
        var viewModel = new MainWindowViewModel(
            composition,
            new FixedStartupLibraryFactory(library, playlist));
        viewModel.StartupUpdateWorkflow.NotifyClosing();
        viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(false);
        viewModel.PlaylistWorkspace.SetPlaylistSummaryMode(true);

        var ownership = new MainWindowTestLifetime(viewModel, lifetime.ShutdownRequested.Task);
        MainWindow? window = null;
        try
        {
            Task initialization = viewModel.ShellActivationWorkflow.ActivateRenderedShell(
                () => { },
                action => action(),
                () => false);
            TestUiDispatcherHost.AwaitTaskOnDispatcher(
                initialization,
                "MainWindowPlaylistWorkspaceWpfTests.main-window-initialization");
            Assert.IsTrue(viewModel.IsInitializationCompleted);

            window = ownership.CreateWindow(() => new MainWindow(
                viewModel,
                settingsWindowCreated: null,
                playlistWorkspaceDialogService: actualRouteDialogService,
                mainWindowForegroundTerminal: foregroundTerminal));
            RoutedEventHandler ensureNonActivatingPosition = (_, _) =>
                window.Dispatcher.BeginInvoke(
                    DispatcherPriority.Render,
                    new Action(() => ReassertNonActivatingPosition(window)));
            window.Loaded += ensureNonActivatingPosition;
            try
            {
                windowTest.ShowAndWaitForContentRendered(window);
            }
            finally
            {
                window.Loaded -= ensureNonActivatingPosition;
            }
            return new ActualMainWindowFixture(
                root,
                viewModel,
                window!,
                playlist,
                library,
                table,
                lifetime,
                modalPreparation,
                ownership);
        }
        catch (Exception failure)
        {
            try { ownership.Dispose(); }
            catch (Exception cleanupFailure) { failure.Data["MainWindowTestCleanupFailure"] = cleanupFailure.ToString(); }
            try { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
            catch (Exception cleanupFailure) { failure.Data["MainWindowFixtureDirectoryCleanupFailure"] = cleanupFailure.ToString(); }
            throw;
        }
    }

    // ShowDialogはmanaged IsEnabledを変えずにowner HWNDを無効化するため、実native状態を観測します。
    [DllImport("user32.dll", EntryPoint = "IsWindowEnabled")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsNativeWindowEnabled(IntPtr window);

    private static ModalObservation<PlaylistImportDialog> OpenImportDialog(
        ActualMainWindowFixture fixture, string operation,
        Action<PlaylistImportDialog, ModalObservation<PlaylistImportDialog>> drive)
    {
        ModalObservation<PlaylistImportDialog> observation = OpenDialog(
            fixture.Window,
            (CustomTableView)fixture.Window.FindName("customTablePlaylistSummary"),
            CreatePlaylistSummaryRow(fixture.Table),
            "Resources.Playlist_import_title", operation, drive,
            () =>
            {
                var root = (TreeViewItem)fixture.Window.FindName("treeViewItemPlaylist");
                ContextMenu menu = root.ContextMenu;
                Assert.IsNotNull(menu, "プレイリストルートにインポートメニューを接続します。");
                Assert.AreSame(fixture.Window.FindResource("treeViewPlaylistRootContextMenu"), menu);
                Assert.IsNull(fixture.Window.FindName("playlistImportButton"), "ツリー上部に独立ボタンを残しません。");
                menu.PlacementTarget = root;
                OpenContextMenu(menu);
                MenuItem import = FindMenuItemByHeaderBindingPath(menu, "Resources.Playlist_import_title");
                Assert.IsTrue(import.IsEnabled);
                Assert.IsTrue(RaiseMenuClick(import).Handled);
            });
        Task cleanup = typeof(MainWindow).GetField("playlistImportDialogCleanupTask", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(fixture.Window) as Task
            ?? throw new AssertFailedException("インポート画面の表示回収Taskがありません。");
        TestUiDispatcherHost.AwaitPresentationOnDispatcher(cleanup, operation + ".modal-cleanup");
        return observation;
    }

    private static ModalObservation<PlaylistPropertyDialog> OpenPropertyDialog(
        MainWindow owner,
        CustomTableView summary,
        PlaylistSummaryRow row,
        string operationName,
        Action<PlaylistPropertyDialog, ModalObservation<PlaylistPropertyDialog>>? drive = null)
    {
        drive ??= (dialog, _) =>
        {
            RaiseButtonClick(FindAutomationButton(dialog, "PlaylistPropertyCancel"));
        };
        return OpenDialog(
            owner,
            summary,
            row,
            commandResourcePath: "Resources.Property",
            operationName,
            drive);
    }

    private static ModalObservation<PlaylistSummaryBulkEditDialog> OpenBulkDialog(
        MainWindow owner,
        CustomTableView summary,
        PlaylistSummaryRow row,
        string operationName,
        Action<PlaylistSummaryBulkEditDialog, ModalObservation<PlaylistSummaryBulkEditDialog>>? drive = null)
    {
        drive ??= (dialog, observation) =>
        {
            var viewModel =
                (PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel)dialog.DataContext;
            viewModel.BmtOutputOption = viewModel.OnOption;
            RaiseButtonClick(FindAutomationButton(dialog, "PlaylistSummaryApplyBmtOutput"));
            dialog.WaitForApplyCompletionAsync().ContinueWith(
                _ => observation.CallbackGate.Queue(
                    DispatcherPriority.Loaded,
                    () =>
                    {
                        observation.VisibleAfterApply = dialog.IsVisible;
                        RaiseButtonClick(FindAutomationButton(dialog, "PlaylistSummaryClose"));
                    }),
                TaskScheduler.Default);
        };
        return OpenDialog(
            owner,
            summary,
            row,
            commandResourcePath: "Resources.Playlist_summary_bulk_edit",
            operationName,
            drive);
    }

    private static ModalObservation<TWindow> OpenDialog<TWindow>(
        MainWindow owner,
        CustomTableView summary,
        PlaylistSummaryRow row,
        string commandResourcePath,
        string operationName,
        Action<TWindow, ModalObservation<TWindow>> drive,
        Action? invoke = null)
        where TWindow : Window
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandResourcePath);
        var opened = new TaskCompletionSource<ModalObservation<TWindow>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ModalObservation<TWindow>? observation = null;
        var callbackGate = new DispatcherCallbackGate(owner.Dispatcher);

        void ObserveDialog()
        {
            if (!callbackGate.IsOpen)
            {
                return;
            }
            TWindow? dialog = Application.Current.Windows
                .OfType<TWindow>()
                .FirstOrDefault(candidate => candidate.IsVisible && candidate.IsLoaded
                    && candidate.ActualWidth > 0 && candidate.ActualHeight > 0);
            if (dialog == null)
            {
                callbackGate.Queue(
                    DispatcherPriority.Loaded,
                    ObserveDialog);
                return;
            }

            observation = new ModalObservation<TWindow>(
                dialog,
                dialog.Owner,
                IsNativeWindowEnabled(new WindowInteropHelper(owner).Handle),
                dialog.DataContext,
                callbackGate);
            dialog.DataContextChanged += observation.HandleDataContextChanged;
            try
            {
                drive(dialog, observation);
                opened.TrySetResult(observation);
            }
            catch (Exception exception)
            {
                // callbackの失敗を待機Taskへ渡し、nested modalを実際に閉じる。
                // Dispatcher例外の捕捉だけで、来ないopened通知を待ち続けない。
                opened.TrySetException(exception);
                try
                {
                    if (dialog is PlaylistPropertyDialog propertyDialog)
                    {
                        propertyDialog.CloseForOwnerShutdown();
                    }
                    else if (dialog is PlaylistSummaryBulkEditDialog bulkDialog)
                    {
                        bulkDialog.CloseForOwnerShutdown();
                    }
                    else { dialog.Close(); }
                }
                catch (Exception cleanupFailure)
                {
                    exception.Data["ModalCleanupFailure"] = cleanupFailure;
                }
            }
        }

        try
        {
            callbackGate.Queue(DispatcherPriority.Loaded, ObserveDialog);
            if (invoke != null)
            {
                invoke();
            }
            else
            {
                var menu = (ContextMenu)owner.FindResource("playlistSummaryContextMenu");
                menu.PlacementTarget = summary;
                menu.Tag = new CustomTableContextMenuContext(row, 0);
                MenuItem command = FindMenuItemByHeaderBindingPath(menu, commandResourcePath);
                RaiseMenuClick(command);
            }

            TestUiDispatcherHost.AwaitTaskOnDispatcher(opened.Task, operationName + ".opened");
            observation = opened.Task.GetAwaiter().GetResult();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(
                observation.DataContextDetached.Task,
                operationName + ".cleanup");
            observation.Window.DataContextChanged -= observation.HandleDataContextChanged;
            return observation;
        }
        finally
        {
            callbackGate.Close();
            if (observation != null)
            {
                observation.Window.DataContextChanged -= observation.HandleDataContextChanged;
            }
        }
    }

    private static Button FindAutomationButton(DependencyObject root, string automationId)
    {
        Button? button = FindDescendants<Button>(root)
            .FirstOrDefault(candidate => string.Equals(
                AutomationProperties.GetAutomationId(candidate),
                automationId,
                StringComparison.Ordinal));
        return button
            ?? throw new AssertFailedException($"Button '{automationId}' was not materialized.");
    }

    private static PropertyNavigationObservation ObservePropertyNavigation(
        FrameworkElement navigation,
        FrameworkElement contentHost)
    {
        AutomationPeer navigationPeer = UIElementAutomationPeer.CreatePeerForElement(navigation)
            ?? throw new AssertFailedException("Playlist property navigation automation was not materialized.");
        ISelectionProvider selectionProvider = navigationPeer.GetPattern(PatternInterface.Selection) as ISelectionProvider
            ?? throw new AssertFailedException("Playlist property navigation does not expose Selection automation.");
        Assert.IsFalse(selectionProvider.CanSelectMultiple);
        AutomationPeer[] navigationItems = navigationPeer.GetChildren()?.ToArray()
            ?? throw new AssertFailedException("Playlist property navigation items were not exposed to Automation.");
        Assert.AreEqual(3, navigationItems.Length, "Playlist property navigation must expose three category peers.");
        PropertyNavigationItem[] selectionItems = navigationItems
            .Select(CreatePropertyNavigationItem)
            .ToArray();
        Assert.IsTrue(selectionItems.All(item => !string.IsNullOrWhiteSpace(item.Peer.GetName())));
        Assert.AreEqual(1, selectionItems.Count(item => item.Provider.IsSelected));
        Assert.AreEqual(1, selectionProvider.GetSelection().Length);

        PropertyNavigationItem initiallySelectedItem = selectionItems.Single(item => item.Provider.IsSelected);
        PropertyNavigationCategory initialCategory = IdentifyVisiblePropertyCategory(contentHost);

        var itemsByCategory = new Dictionary<PropertyNavigationCategory, PropertyNavigationItem>();
        Assert.IsTrue(
            itemsByCategory.TryAdd(initialCategory, initiallySelectedItem),
            "The initially selected property provider must map to one observable content role.");
        foreach (PropertyNavigationItem item in selectionItems)
        {
            if (ReferenceEquals(item, initiallySelectedItem))
            {
                continue;
            }

            PropertyNavigationCategory category;
            if (!item.Peer.IsEnabled())
            {
                AssertAuthorityDefinedCustomFolderAvailability(navigation, item.Peer);
                PropertyNavigationItem selectedBeforeAttempt = selectionItems.Single(candidate => candidate.Provider.IsSelected);
                bool rejected = false;
                try
                {
                    item.Provider.Select();
                }
                catch (ElementNotEnabledException)
                {
                    rejected = true;
                }
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsTrue(
                    rejected || !item.Provider.IsSelected,
                    "A disabled property navigation provider must reject Automation selection.");
                Assert.IsTrue(
                    selectedBeforeAttempt.Provider.IsSelected,
                    "A rejected disabled property navigation selection must leave the current category selected.");
                category = PropertyNavigationCategory.CustomFolder;
            }
            else
            {
                item.Provider.Select();
                TestUiDispatcherHost.ProcessQueuedPresentation();
                category = IdentifyVisiblePropertyCategory(contentHost);
            }

            Assert.IsTrue(
                itemsByCategory.TryAdd(category, item),
                $"Multiple navigation items expose the {category} property content.");
        }

        Assert.AreEqual(3, itemsByCategory.Count, "Property navigation categories must map to unique observable content roles.");
        initiallySelectedItem.Provider.Select();
        TestUiDispatcherHost.ProcessQueuedPresentation();
        return new PropertyNavigationObservation(selectionProvider, itemsByCategory);
    }

    private static PropertyNavigationItem CreatePropertyNavigationItem(AutomationPeer peer)
        => new(
            peer,
            peer.GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider
                ?? throw new AssertFailedException("A property navigation item lacks SelectionItem automation."));

    private static void AssertSelectedPropertyCategory(
        PropertyNavigationObservation navigation,
        PropertyNavigationCategory expectedCategory,
        string? message = null)
    {
        string assertionMessage = message ?? $"Property navigation category {expectedCategory} must be selected.";
        Assert.AreEqual(1, navigation.SelectionProvider.GetSelection().Length, assertionMessage);
        Assert.AreEqual(1, navigation.ItemsByCategory.Values.Count(item => item.Provider.IsSelected), assertionMessage);
        Assert.IsTrue(navigation.ItemsByCategory[expectedCategory].Provider.IsSelected, assertionMessage);
    }

    private static PropertyNavigationCategory IdentifyVisiblePropertyCategory(FrameworkElement contentHost)
    {
        var visibleCategories = new List<PropertyNavigationCategory>();
        if (HasVisibleBinding(contentHost, nameof(PlaylistPropertyDialogViewModel.name)))
        {
            visibleCategories.Add(PropertyNavigationCategory.General);
        }

        if (HasVisibleBinding(contentHost, nameof(PlaylistPropertyDialogViewModel.folder_sort_key))
            || HasVisibleBinding(contentHost, nameof(PlaylistPropertyDialogViewModel.folder_order)))
        {
            visibleCategories.Add(PropertyNavigationCategory.Folder);
        }

        if (HasVisibleBinding(contentHost, nameof(PlaylistPropertyDialogViewModel.output_dir)))
        {
            visibleCategories.Add(PropertyNavigationCategory.CustomFolder);
        }

        Assert.AreEqual(
            1,
            visibleCategories.Count,
            "Exactly one authority-backed Property category content role must be visible.");
        return visibleCategories[0];
    }

    private static bool HasVisibleBinding(DependencyObject root, string path)
    {
        return FindDescendants<FrameworkElement>(root)
            .Any(element => element.IsVisible
                && element.ActualWidth > 0d
                && element.ActualHeight > 0d
                && HasBindingPath(element, path));
    }

    private static bool HasBindingPath(FrameworkElement element, string path)
    {
        LocalValueEnumerator localValues = element.GetLocalValueEnumerator();
        while (localValues.MoveNext())
        {
            if (localValues.Current.Value is BindingExpressionBase expression
                && expression.ParentBindingBase is Binding binding
                && string.Equals(binding.Path?.Path, path, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void AssertAuthorityDefinedCustomFolderAvailability(
        FrameworkElement navigation,
        AutomationPeer expectedProvider)
    {
        FrameworkElement[] availabilityHosts = FindDescendants<FrameworkElement>(navigation)
            .Where(element => HasBindingPath(
                element,
                nameof(PlaylistPropertyDialogViewModel.OperationModeLR2DB)))
            .ToArray();
        Assert.AreEqual(
            1,
            availabilityHosts.Length,
            "The conditionally available property category must expose one authority-backed availability binding.");
        Assert.IsFalse(
            availabilityHosts[0].IsEnabled,
            "The authority-backed custom-folder availability binding must be disabled for a rejected provider.");
        AutomationPeer availabilityPeer = UIElementAutomationPeer.CreatePeerForElement(availabilityHosts[0])
            ?? throw new AssertFailedException(
                "The authority-backed custom-folder availability host must expose the disabled SelectionItem provider.");
        Assert.AreEqual(
            expectedProvider.GetAutomationControlType(),
            availabilityPeer.GetAutomationControlType(),
            "The disabled provider must retain the authority-backed custom-folder Automation role.");
        Assert.AreEqual(
            expectedProvider.GetBoundingRectangle(),
            availabilityPeer.GetBoundingRectangle(),
            "The disabled provider must be the authority-backed custom-folder navigation role.");
    }

    private enum PropertyNavigationCategory
    {
        General,
        Folder,
        CustomFolder,
    }

    private sealed record PropertyNavigationItem(
        AutomationPeer Peer,
        ISelectionItemProvider Provider);

    private sealed record PropertyNavigationObservation(
        ISelectionProvider SelectionProvider,
        IReadOnlyDictionary<PropertyNavigationCategory, PropertyNavigationItem> ItemsByCategory);

    private static MenuItem FindMenuItemByHeaderBindingPath(
        ItemsControl root,
        string expectedBindingPath)
    {
        MenuItem? command = root.Items
            .OfType<MenuItem>()
            .SingleOrDefault(item => BindingOperations.GetBindingBase(
                    item,
                    HeaderedItemsControl.HeaderProperty) is Binding binding
                && string.Equals(
                    binding.Path?.Path,
                    expectedBindingPath,
                    StringComparison.Ordinal));
        return command
            ?? throw new AssertFailedException(
                $"Menu item with header binding '{expectedBindingPath}' was not materialized.");
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root == null)
        {
            yield break;
        }
        var pending = new Stack<DependencyObject>();
        var visited = new HashSet<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            DependencyObject current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }
            if (current is T typed)
            {
                yield return typed;
            }
            if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
            {
                for (int index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                {
                    pending.Push(VisualTreeHelper.GetChild(current, index));
                }
            }
            foreach (object logicalChild in LogicalTreeHelper.GetChildren(current))
            {
                if (logicalChild is DependencyObject dependencyObject)
                {
                    pending.Push(dependencyObject);
                }
            }
        }
    }

    private static void RaiseButtonClick(Button button)
        => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

    private static PlaylistSummaryRow CreatePlaylistSummaryRow(BMSTable table)
    {
        return new PlaylistSummaryRow
        {
            PlaylistId = table.playlist_id,
            Name = table.name,
            BmtSort = table.bmt_sort ?? int.MaxValue,
            IsBmtOutput = table.is_bmt_output != false,
            TableRef = table
        };
    }

    private sealed class RecordingPlaylistWorkspaceDialogService : IUiDialogService
    {
        internal UiDialogResult ConfirmationResult { get; set; } =
            UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel);

        internal UiDialogResult MessageResult { get; set; } =
            UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

        internal List<UiConfirmationRequest> ConfirmationRequests { get; } = [];

        internal List<UiMessageRequest> MessageRequests { get; } = [];

        internal TaskCompletionSource<UiConfirmationRequest> ConfirmationShown { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<UiMessageRequest> MessageShown { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<UiDialogResult> ShowMessageAsync(
            UiMessageRequest request,
            CancellationToken cancellationToken = default)
        {
            MessageRequests.Add(request);
            MessageShown.TrySetResult(request);
            return Task.FromResult(MessageResult);
        }

        public Task<UiDialogResult> ConfirmAsync(
            UiConfirmationRequest request,
            CancellationToken cancellationToken = default)
        {
            ConfirmationRequests.Add(request);
            ConfirmationShown.TrySetResult(request);
            return Task.FromResult(ConfirmationResult);
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
            UiWindowDialogRequest<TWindow, TResult> request,
            CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(
            UiFilePickerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(
            UiFolderPickerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(
            UiSaveFilePickerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiProgressResult> RunWithProgressAsync(
            UiProgressRequest request,
            Func<UiProgressContext, Task> operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ModalObservation<TWindow>
        where TWindow : Window
    {
        internal ModalObservation(
            TWindow window,
            Window owner,
            bool ownerEnabled,
            object dataContext,
            DispatcherCallbackGate callbackGate)
        {
            Window = window;
            Owner = owner;
            OwnerEnabled = ownerEnabled;
            DataContext = dataContext;
            CallbackGate = callbackGate;
        }

        internal TWindow Window { get; }

        internal Window Owner { get; }

        internal bool OwnerEnabled { get; }

        internal object DataContext { get; }

        internal DispatcherCallbackGate CallbackGate { get; }

        internal bool VisibleAfterApply { get; set; }

        internal bool ApplyWasPending { get; set; }

        internal bool OwnerCloseWasCanceled { get; set; }

        internal bool ApplyCompletedAtTerminalRequest { get; set; }

        internal int ShutdownCountBeforeApplyCompletion { get; set; }

        internal int ShutdownCountBeforeLockRelease { get; set; }

        internal int DataContextDetachCount { get; private set; }

        internal TaskCompletionSource<object?> DataContextDetached { get; } = NewCompletion();

        internal void HandleDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue != null)
            {
                return;
            }
            DataContextDetachCount++;
            DataContextDetached.TrySetResult(null);
        }
    }

    /// <summary>モーダル観測が所有する未開始callbackを取消し、watchdogや終端回収後に観測待ちを再投入しません。</summary>
    private sealed class DispatcherCallbackGate
    {
        private readonly Dispatcher dispatcher;
        private readonly object sync = new();
        private readonly HashSet<DispatcherOperation> pendingOperations = [];
        private bool isOpen = true;

        internal DispatcherCallbackGate(Dispatcher dispatcher)
        {
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        internal bool IsOpen
        {
            get
            {
                lock (sync)
                {
                    return isOpen;
                }
            }
        }

        internal void Queue(DispatcherPriority priority, Action callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            DispatcherOperation? operation = null;
            lock (sync)
            {
                if (!isOpen)
                {
                    return;
                }

                operation = dispatcher.BeginInvoke(
                    priority,
                    new Action(() => Execute(operation!, callback)));
                pendingOperations.Add(operation!);
            }
        }

        internal void Close()
        {
            DispatcherOperation[] operations;
            lock (sync)
            {
                if (!isOpen)
                {
                    return;
                }

                isOpen = false;
                operations = pendingOperations.ToArray();
                pendingOperations.Clear();
            }

            foreach (DispatcherOperation operation in operations)
            {
                operation.Abort();
            }
        }

        private void Execute(DispatcherOperation operation, Action callback)
        {
            lock (sync)
            {
                pendingOperations.Remove(operation);
                if (!isOpen)
                {
                    return;
                }
            }

            callback();
        }
    }

    private sealed class ModalPreparationRecorder
    {
        private readonly List<Window> preparedWindows = [];

        internal int Count => preparedWindows.Count;

        internal void Record(Window window)
        {
            ArgumentNullException.ThrowIfNull(window);
            preparedWindows.Add(window);
        }

        internal void AssertLatest(Window expectedWindow, int expectedCount)
        {
            ArgumentNullException.ThrowIfNull(expectedWindow);
            Assert.AreEqual(expectedCount, Count);
            Assert.AreSame(expectedWindow, preparedWindows[^1]);
        }
    }

    private sealed class CallbackDisposable(Action dispose) : IDisposable
    {
        private Action? dispose = dispose ?? throw new ArgumentNullException(nameof(dispose));

        public void Dispose()
        {
            Interlocked.Exchange(ref dispose, null)?.Invoke();
        }
    }

    private sealed class ActualMainWindowFixture
    {
        internal ActualMainWindowFixture(
            string root,
            MainWindowViewModel viewModel,
            MainWindow window,
            TestBmsPlaylist playlist,
            TestBmsLibrary library,
            BMSTable table,
            RecordingApplicationLifetime lifetime,
            ModalPreparationRecorder modalPreparation,
            MainWindowTestLifetime ownership)
        {
            Root = root;
            ViewModel = viewModel;
            Window = window;
            Playlist = playlist;
            Library = library;
            Table = table;
            Lifetime = lifetime;
            ModalPreparation = modalPreparation;
            this.ownership = ownership;
        }

        internal string Root { get; }

        internal MainWindowViewModel ViewModel { get; }

        internal MainWindow Window { get; }

        internal TestBmsPlaylist Playlist { get; }

        internal TestBmsLibrary Library { get; }

        internal BMSTable Table { get; }

        internal RecordingApplicationLifetime Lifetime { get; }

        internal ModalPreparationRecorder ModalPreparation { get; }

        private readonly MainWindowTestLifetime ownership;

        internal void Close()
        {
            try { ownership.Dispose(); }
            finally { if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); } }
        }
    }

    private sealed class FixedStartupLibraryFactory : IStartupLibraryFactory
    {
        internal FixedStartupLibraryFactory(TestBmsLibrary library, TestBmsPlaylist playlist)
        {
            Library = library;
            Playlist = playlist;
        }

        private TestBmsLibrary Library { get; }

        private TestBmsPlaylist Playlist { get; }

        public BMSLibrary CreateBmsLibrary(LibraryProfile libraryProfile) => Library;

        public BMSPlaylist CreateBmsPlaylist(LibraryProfile libraryProfile, BMSLibrary library) => Playlist;
    }

    private sealed class RecordingApplicationLifetime : IApplicationLifetimePort
    {
        internal TaskCompletionSource<object?> ShutdownRequested { get; } = NewCompletion();

        internal int RequestShutdownCount { get; private set; }

        internal Action? RequestShutdownAction { get; set; }

        public bool IsFirstStartup => false;

        public void CompleteFirstStartup()
        {
        }

        public void MarkCoordinatedShutdownStarted(string reason)
        {
        }

        public void ExitForUnrestoredDestination(System.Exception failure)
        => throw new System.InvalidOperationException("Unexpected unrestored destination exit.", failure);

        public void RequestShutdown()
        {
            RequestShutdownCount++;
            ShutdownRequested.TrySetResult(null);
            RequestShutdownAction?.Invoke();
        }

        public Task RestartApplicationAsync() => Task.CompletedTask;
    }

    private static void MaterializeTreeItems(TreeViewItem root)
    {
        root.ApplyTemplate();
        root.Measure(new Size(640d, 480d));
        root.Arrange(new Rect(0d, 0d, 640d, 480d));
        // 必要なbinding/renderを処理して実containerを確定する。全ApplicationIdleは
        // MediaContextの入力待ちにも依存するため、表示内容の完了条件には使わない。
        root.Dispatcher.Invoke(DispatcherPriority.Loaded, new Action(root.UpdateLayout));
    }

    private static void MaterializeMenuItems(ItemsControl menu)
    {
        menu.ApplyTemplate();
        menu.Measure(new Size(640d, 480d));
        menu.Arrange(new Rect(0d, 0d, 640d, 480d));
        menu.Dispatcher.Invoke(DispatcherPriority.Loaded, new Action(menu.UpdateLayout));
    }

    private static MenuItem GetOrGenerateMenuItem(ItemsControl owner, object item)
    {
        var generated = owner.ItemContainerGenerator.ContainerFromItem(item) as MenuItem;
        if (generated != null)
        {
            return generated;
        }

        int index = owner.Items.IndexOf(item);
        Assert.IsTrue(index >= 0, $"The menu item {item} was not present in its ItemsSource.");
        IItemContainerGenerator generator = owner.ItemContainerGenerator;
        using (generator.StartAt(
            generator.GeneratorPositionFromIndex(index),
            GeneratorDirection.Forward,
            allowStartAtRealizedItem: true))
        {
            DependencyObject candidate = generator.GenerateNext(out bool newlyRealized);
            if (newlyRealized)
            {
                generator.PrepareItemContainer(candidate);
            }
        }

        generated = owner.ItemContainerGenerator.ContainerFromItem(item) as MenuItem;
        return generated!;
    }

    private static void OpenContextMenu(ContextMenu menu)
    {
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, menu));
        TestUiDispatcherHost.ProcessQueuedPresentation();
    }

    private static RoutedEventArgs RaiseMenuClick(MenuItem item)
    {
        var args = new RoutedEventArgs(MenuItem.ClickEvent, item);
        item.RaiseEvent(args);
        return args;
    }

    private static MenuItem FindMenuItem(ItemsControl root, string name)
    {
        MenuItem? result = FindMenuItemOrNull(root, name);
        if (result != null)
        {
            return result;
        }
        throw new AssertFailedException($"Menu item '{name}' was not found.");
    }

    private static MenuItem? FindMenuItemOrNull(ItemsControl root, string name)
    {
        foreach (MenuItem item in root.Items.OfType<MenuItem>())
        {
            if (item.Name == name)
            {
                return item;
            }
            if (item.Items.Count > 0)
            {
                MenuItem? nested = FindMenuItemOrNull(item, name);
                if (nested != null)
                {
                    return nested;
                }
            }
        }
        return null;
    }
}
