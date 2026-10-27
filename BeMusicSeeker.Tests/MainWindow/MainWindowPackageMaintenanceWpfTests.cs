using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>保守操作の実ファイル・DB反映と、compiledメニューから管理主体への接続を検証します。</summary>
/// <remarks>要求の可否とreceipt単体は下位へ任せ、ここでは実画面の選択・編集保持やクリックの配送先と非同期終端を観測します。</remarks>
[TestClass]
// 実VMの終了がプロセス共通のSQLite接続と音声資源を解放するため、他の実DBケースと並列にしません。
[DoNotParallelize]
public sealed partial class MainWindowPackageMaintenanceWpfTests
{
    /// <summary>実際の表ヘッダーの並べ替え要求から通常一覧の全順を確認し、行の位置が変わっても所持譜面の識別と非空の選択を保つことを確認します。</summary>
    [TestMethod]
    public void RegularHeaderSort_CompiledTablePreservesNonemptySelectionThroughActualPresentation()
    {
        string directory = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RegularSortWpf_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TestBmsLibrary? library = null;
        ChartFile[] charts = [];
        Settings settings = TestBmsFactory.CreateRootSettings([directory]);
        settings.StartupSelectInstallPending = false;
        try
        {
            MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(settings, (scope, vm, window) =>
            {
                Assert.IsNotNull(library);
                var table = (CustomTableView)window.FindName("customTableView");
                // コンパイル済みの画面を表示し、このテストの対象外である起動処理は開始しない。
                object content = window.Content;
                window.Content = null;
                var host = new Window { Content = content, DataContext = vm, Width = 1000, Height = 700 };
                scope.ShowAndWaitForContentRendered(host);
                var navigationStarted = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<RegularChartTreeNavigationPresentationRequestedEventArgs> navigating = (_, request) => navigationStarted.TrySetResult(request.Presentation);
                vm.RegularChartList.TreeNavigationPresentationRequested += navigating;
                try
                {
                    Assert.IsTrue(vm.RegularChartList.NavigateTree(null));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(navigationStarted.Task, "normal initial navigation started");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(navigationStarted.Task.Result, "normal initial actual navigation presentation");
                    AwaitBound();
                }
                finally { vm.RegularChartList.TreeNavigationPresentationRequested -= navigating; }
                CollectionAssert.AreEqual(new[] { "Alpha", "Beta", "Zeta" }, table.ItemsSource.Cast<LibraryChartRow>().Select(row => row.Title).ToArray());
                string alphaPath = charts[0].Path;
                table.SelectRowsByPredicate(row => row is LibraryChartRow chart && chart.path == alphaPath);
                var selected = (LibraryChartRow)table.GetSelectedRowsSnapshot().Single();
                OwnedChartToken token = selected.Chart.Token;
                Assert.AreEqual(0, table.SelectedIndex);
                Assert.IsNotNull(token);
                var requested = new TaskCompletionSource<RegularChartSortRefreshRequestedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<RegularChartSortRefreshRequestedEventArgs> sortStarted = (_, request) => requested.TrySetResult(request);
                int replacing = 0;
                int preserving = 0;
                bool notificationsOnUi = true;
                EventHandler rowsReplacing = (_, _) => { replacing++; notificationsOnUi &= TestUiDispatcherHost.Dispatcher.CheckAccess(); };
                EventHandler<MainChartListSelectionRestoration> rowsPreserving = (_, _) => { preserving++; notificationsOnUi &= TestUiDispatcherHost.Dispatcher.CheckAccess(); };
                vm.RegularChartList.SortRefreshRequested += sortStarted;
                vm.MainChartList.RowsReplacing += rowsReplacing;
                vm.MainChartList.RowsSelectionPreserving += rowsPreserving;
                try
                {
                    CustomTableColumn column = table.Columns.Single(column => column.SortMemberPath == nameof(LibraryChartRow.Artist));
                    // 座標によるヘッダー入力の補助がないため、実コントロールのRequestSortから表示採用までを検査する。
                    // マウス位置からRequestSortへの接続は静的に確認し、このテストでは選択状態やイベントの保持先を直接変更しない。
                    MethodInfo requestSort = typeof(CustomTableView).GetMethod("RequestSort", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? throw new AssertFailedException("実header sort producerがありません。");
                    requestSort.Invoke(table, [column]);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(requested.Task, "compiled header sort actual request");
                    RegularChartSortRefreshRequestedEventArgs receipt = requested.Task.Result;
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(receipt.Presentation, "compiled header sort actual presentation terminal");
                    Assert.IsTrue(receipt.Presentation.IsCompletedSuccessfully);
                    AwaitBound();
                    Assert.AreEqual(nameof(LibraryChartRow.Artist), receipt.SortRequest.ColumnName);
                    Assert.AreEqual(ListSortDirection.Ascending, receipt.SortRequest.Direction);
                    CollectionAssert.AreEqual(new[] { charts[2].Path, charts[1].Path, charts[0].Path },
                        table.ItemsSource.Cast<LibraryChartRow>().Select(row => row.path).ToArray());
                    Assert.AreEqual(nameof(LibraryChartRow.Artist), table.SortColumnName);
                    Assert.AreEqual(ListSortDirection.Ascending, table.SortDirection);
                    Assert.AreEqual(2, table.SelectedIndex, "Alphaは同じidentityのまま実位置を変えます。");
                    var after = (LibraryChartRow)table.GetSelectedRowsSnapshot().Single();
                    Assert.AreEqual(alphaPath, after.path);
                    Assert.AreSame(token, after.Chart.Token);
                    Assert.AreEqual(1, replacing);
                    Assert.AreEqual(1, preserving);
                    Assert.IsTrue(notificationsOnUi, "Rows交換準備とPreserve通知の全体をUIで採用します。");
                }
                finally
                {
                    vm.RegularChartList.SortRefreshRequested -= sortStarted;
                    vm.MainChartList.RowsReplacing -= rowsReplacing;
                    vm.MainChartList.RowsSelectionPreserving -= rowsPreserving;
                    if (requested.Task.IsCompletedSuccessfully) { TestUiDispatcherHost.AwaitTaskOnDispatcher(requested.Task.Result.Presentation, "header sort task cleanup"); }
                }
                void AwaitBound()
                {
                    var bound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var descriptor = DependencyPropertyDescriptor.FromProperty(CustomTableView.ItemsSourceProperty, typeof(CustomTableView));
                    EventHandler changed = (_, _) => { if (ReferenceEquals(table.ItemsSource, vm.MainChartList.Rows)) { bound.TrySetResult(); } };
                    descriptor.AddValueChanged(table, changed);
                    try { changed(table, EventArgs.Empty); TestUiDispatcherHost.AwaitPresentationOnDispatcher(bound.Task, "normal actual table binding"); }
                    finally { descriptor.RemoveValueChanged(table, changed); }
                    Assert.AreSame(vm.MainChartList.Rows, table.ItemsSource);
                }
            }, prepareViewModel: vm =>
            {
                string[] titles = ["Alpha", "Beta", "Zeta"];
                string[] artists = ["Z", "M", "A"];
                charts = titles.Select((title, index) =>
                {
                    string path = Path.Combine(directory, title + ".bms");
                    File.WriteAllText(path, "#PLAYER 1\n#TITLE " + title + "\n#ARTIST " + artists[index] + "\n#BPM 120\n");
                    return BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path));
                }).ToArray();
                string songDb = Path.Combine(directory, "song.db");
                BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDb, db =>
                { db.CreateTable<LR2SongDB.song>(); db.CreateTable<LR2SongDB.folder>(); db.CreateTable<LR2SongDBExtended.maintenance>(); });
                new BmsLibraryDbGateway(songDb).UpsertSongs(charts);
                library = MainWindowViewModelTestFactory.CreateLibrary(songDb, settings, vm);
                library.BmsCharts = charts;
                ((IStartupLibraryApplicationPort)vm).AttachStartupLibrary(library);
            });
        }
        finally { library?.RequestShutdown("regular_sort_wpf"); Directory.Delete(directory, recursive: true); }
    }

    /// <summary>実DBの混在純移転が安定entryと表示へ届き、無関係な表示と編集中入力を保持します。</summary>
    [TestMethod]
    public void InstalledMixedFolderRelocationKeepsRowsSelectionAndUncommittedEditor()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        string root = Path.Combine(Path.GetTempPath(), "installed-current-wpf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values => values.OperationModeLR2DB = false);
        settings.StartupSelectInstallPending = false;
        try
        {
            MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
                settings,
                (scope, viewModel, window) =>
                {
                    string source = Path.Combine(root, "source");
                    Directory.CreateDirectory(source);
                    string bmsPath = Path.Combine(source, "chart.bms");
                    string bmsonPath = Path.Combine(source, "chart.bmson");
                    File.WriteAllText(bmsPath, "#PLAYER 1\n#TITLE A current\n#BPM 120\n");
                    File.WriteAllText(bmsonPath, "{}");
                    ChartFile bms = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsPath));
                    ChartFile bmson = BmsonChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsonPath));
                    string dbPath = Path.Combine(root, "song.db");
                    using (var db = new LR2SongDBExtended(dbPath))
                    {
                        db.CreateTable<LR2SongDB.song>();
                        db.CreateTable<LR2SongDBExtended.bmson_song>();
                    }
                    TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(dbPath, settings, viewModel);
                    library.BmsCharts = [bms];
                    library.BmsonCharts = [bmson];
                    bms = library.BmsCharts.Single();
                    bmson = library.BmsonCharts.Single();
                    using (var db = new LR2SongDBExtended(dbPath))
                    {
                        db.CreateTable<LR2SongDB.song>();
                        db.CreateTable<LR2SongDBExtended.bmson_song>();
                        db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(bms), typeof(LR2SongDB.song));
                        db.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(bmson), typeof(LR2SongDBExtended.bmson_song));
                    }
                    var first = PackageChartEntry.FromChart(bms);
                    var second = PackageChartEntry.FromChart(bmson);
                    var package = ChartPackage.FromChartEntries([first, second]);
                    package.path = source;
                    library.ChartPackagesInstalled = new System.Collections.ObjectModel.ObservableCollection<ChartPackage>([package]);
                    ((IStartupLibraryApplicationPort)viewModel).AttachStartupLibrary(library);
                    var table = (CustomTableView)window.FindName("customTableView");
                    object content = window.Content;
                    window.Content = null;
                    var host = new Window { Content = content, DataContext = viewModel, Width = 1000, Height = 700 };
                    scope.ShowAndWaitForContentRendered(host);
                    var rowsReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    System.ComponentModel.PropertyChangedEventHandler rowsChanged = (_, args) =>
                    {
                        if (args.PropertyName == nameof(MainChartListViewModel.Rows))
                        {
                            rowsReady.TrySetResult();
                        }
                    };
                    viewModel.MainChartList.PropertyChanged += rowsChanged;
                    try
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(
                            viewModel.RegularChartList.NavigateInstallAsync(MainViewUpdateMode.NewlyInstalledFolderSelected), "installed navigation");
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(rowsReady.Task, "installed rows applied");
                    }
                    finally
                    {
                        viewModel.MainChartList.PropertyChanged -= rowsChanged;
                    }
                    IList rows = viewModel.MainChartList.Rows;
                    var bindingApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var itemsSource = DependencyPropertyDescriptor.FromProperty(CustomTableView.ItemsSourceProperty, typeof(CustomTableView));
                    EventHandler bindingChanged = (_, _) =>
                    {
                        if (ReferenceEquals(rows, table.ItemsSource)) { bindingApplied.TrySetResult(); }
                    };
                    itemsSource.AddValueChanged(table, bindingChanged);
                    try
                    {
                        bindingChanged(table, EventArgs.Empty);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(bindingApplied.Task, "installed rows binding applied");
                    }
                    finally { itemsSource.RemoveValueChanged(table, bindingChanged); }
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreSame(rows, table.ItemsSource, "Rows公開と実表bindingの反映を別々に完了させてから編集を開始します。");
                    Assert.AreEqual(2, rows.Count);
                    LibraryChartRow[] realized = rows.Cast<LibraryChartRow>().ToArray();
                    EventHandler<MainChartListCellEditBeginningEventArgs> allowEdit = (_, request) => request.Accepted = true;
                    viewModel.MainChartList.CellEditBeginningRequested += allowEdit;
                    var layout = new CustomTableColumnSettings.ColumnLayout { Width = 180, Visibility = Visibility.Visible };
                    table.Columns = [new CustomTableColumn("Destination", "Destination", layout, 0, null,
                        TextAlignment.Left, row => ((LibraryChartRow)row).instl_dst, editPropertyName: "instl_dst")];
                    table.HandleKeyDown(Key.Down, ModifierKeys.None);
                    Assert.IsTrue(table.HandleKeyDown(Key.F2, ModifierKeys.None));
                    TextBox editor = FindCurrentEditors(table).Single();
                    editor.Text = "uncommitted";
                    int selected = table.SelectedIndex;
                    int rowsReplacing = 0;
                    EventHandler replacing = (_, _) => rowsReplacing++;
                    viewModel.MainChartList.RowsReplacing += replacing;
                    var appliedBatches = new List<NormalLibraryRefreshNotificationBatch>();
                    var entryPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var refreshApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    System.ComponentModel.PropertyChangedEventHandler entryChanged = (_, _) =>
                    {
                        var probe = Task.Run(() =>
                        {
                            using LibraryFileMutationLease lease = library.TryBeginLibraryFileMutation("entry-release-probe", showMessage: false);
                            Assert.IsNotNull(lease, "entry通知時にモデル受付権を解放します。");
                        });
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(probe, "entry release probe");
                        entryPublished.TrySetResult();
                    };
                    EventHandler<NormalLibraryRefreshAppliedEventArgs> applied = (_, args) =>
                    {
                        appliedBatches.Add(args.NotificationBatch);
                        refreshApplied.TrySetResult();
                    };
                    first.PropertyChanged += entryChanged;
                    viewModel.RegularChartList.NormalLibraryRefreshApplied += applied;
                    try
                    {
                        _ = OwnedChartCollectionTestSupport.InvokeCreateInstalledChartLookupSnapshot(library);
                        library.RenameChartFolder(source, "moved");
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(entryPublished.Task, "installed entry publication");
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(refreshApplied.Task, "installed current refresh");
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        string moved = Path.Combine(root, "moved");
                        Assert.AreEqual(0, rowsReplacing);
                        Assert.AreSame(rows, viewModel.MainChartList.Rows);
                        Assert.AreSame(rows, table.ItemsSource);
                        Assert.AreEqual(selected, table.SelectedIndex);
                        CollectionAssert.AreEqual(realized, rows.Cast<LibraryChartRow>().ToArray());
                        var remainingEditors = FindCurrentEditors(table).ToList();
                        Assert.AreEqual(1, remainingEditors.Count);
                        Assert.AreSame(editor, remainingEditors.Single());
                        Assert.AreEqual("uncommitted", editor.Text);
                        foreach (PackageChartEntry entry in new[] { first, second })
                        {
                            Assert.AreEqual(Path.Combine(moved, Path.GetFileName(entry == first ? bmsPath : bmsonPath)), entry.Chart.Path);
                            Assert.AreEqual(entry.Chart.Path, realized.Single(row => row.Chart.Token == entry.Chart.Token).Chart.Path);
                        }
                        Assert.AreEqual(bmsPath, bms.Path);
                        Assert.AreEqual(bmsonPath, bmson.Path);
                        Assert.AreSame(first, package.ChartEntries[0]);
                        Assert.AreSame(second, package.ChartEntries[1]);
                        InstalledChartLookupIndexSnapshot index = OwnedChartCollectionTestSupport.InvokeCreateInstalledChartLookupSnapshot(library);
                        CollectionAssert.AreEqual(new[] { moved }, index.Md5Directories[bms.Md5].ToArray());
                        Assert.AreEqual(2, appliedBatches.SelectMany(batch => batch.ChangedCharts)
                            .Select(chart => chart.Token).Distinct().Count());
                        Assert.IsTrue(appliedBatches.SelectMany(batch => batch.CatalogCommits).SelectMany(commit => commit.Changes)
                            .Any(change => change.Before?.Path == bmsPath && change.After?.Path == first.Chart.Path));
                        using LR2SongDBExtended readback = new BmsLibraryDbGateway(dbPath).OpenSongDbReadOnly();
                        Assert.IsNotNull(readback.Find<LR2SongDB.song>(first.Chart.Path));
                        Assert.IsNotNull(readback.Find<LR2SongDBExtended.bmson_song>(second.Chart.Path));
                    }
                    finally
                    {
                        viewModel.MainChartList.RowsReplacing -= replacing;
                        first.PropertyChanged -= entryChanged;
                        viewModel.RegularChartList.NormalLibraryRefreshApplied -= applied;
                        table.HandleKeyDown(Key.Escape, ModifierKeys.None);
                        viewModel.MainChartList.CellEditBeginningRequested -= allowEdit;
                        library.ChartPackagesInstalled.Clear();
                    }
                });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IEnumerable<TextBox> FindCurrentEditors(DependencyObject parent)
    {
        for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is TextBox editor)
            {
                yield return editor;
            }
            foreach (TextBox nested in FindCurrentEditors(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>実自動改名のDB終端失敗を通知し、通知側が失敗しても確定済み移動・receiptを失わず、通知完了後に受付を解放します。</summary>
    [DataTestMethod]
    [DataRow(false, false)] // 選択フォルダの改名後にDB終端が失敗。
    [DataRow(true, false)] // 全フォルダ要求でも同じ失敗receiptを観測。
    [DataRow(false, true)] // 失敗通知そのものが例外になっても変更事実を保持。
    public void AutoRenameReportsRealSessionReceiptWithoutLosingFacts(bool allFolders, bool reporterThrows)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string root = Path.Combine(Path.GetTempPath(), "FSDB-B-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var dialogs = new FileDbReportRecordingDialogs();
            MainWindowViewModel? viewModel = null;
            try
            {
                string source = Path.Combine(root, "source");
                Directory.CreateDirectory(source);
                string sourceChart = Path.Combine(source, "chart.bms");
                File.WriteAllText(sourceChart, "#PLAYER 1\r\n#TITLE ReceiptTarget\r\n#ARTIST Artist\r\n");
                ChartFile chart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(sourceChart));
                string dbPath = Path.Combine(root, "song.db");
                string lr2Root = Path.Combine(root, "LR2");
                LR2Config config = BmsPlaylistTestSupport.CreateLr2Config(lr2Root, root);
                var configOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(config);
                using (var db = new LR2SongDBExtended(dbPath))
                {
                    db.CreateTable<LR2SongDB.song>();
                    db.CreateTable<LR2SongDB.folder>();
                    db.CreateTable<LR2SongDBExtended.maintenance>();
                    db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(chart), typeof(LR2SongDB.song));
                    db.Execute("CREATE TRIGGER fail_report_finalizer BEFORE INSERT ON folder WHEN NEW.path LIKE '%ReceiptTarget%' BEGIN SELECT RAISE(ABORT, 'consumer-finalizer-marker'); END;");
                }
                var composition = new ApplicationComposition(
                    settingsEditSession: new NoOpSettingsEditSession(MainWindowViewModelTestFactory.CreateIsolatedSettings()),
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(),
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    fileDbMutationDialogService: dialogs);
                var library = new TestBmsLibrary(dbPath, configOwner, null,
                    new ResilientFileMutationService(), dialogs,
                    new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    () => new BmsLibraryOptionsSnapshot
                    {
                        OperationModeLR2DB = true,
                        LR2RootPath = lr2Root,
                        FolderNameFormat = "[%ARTIST%] %TITLE%"
                    }, operationAdmission: composition.OperationAdmission,
                    playlistOperationAdmission: composition.PlaylistOperationAdmission, settings: TestBmsFactory.CreateRootSettings([root]))
                { BmsCharts = [chart] };
                viewModel = composition.CreateMainWindowViewModelForTest();
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                ((IStartupLibraryApplicationPort)viewModel).AttachStartupLibrary(library);
                bool activityInactive = false;
                bool leaseBusy = false;
                dialogs.OnMessage = () =>
                {
                    activityInactive = !viewModel.ChartMutationActivity.IsActive;
                    using LibraryFileMutationLease gate = library.TryBeginLibraryFileMutation("report-probe", showMessage: false);
                    leaseBusy = gate == null;
                };
                if (reporterThrows)
                {
                    dialogs.MessageFailure = new IOException("reporter-marker");
                }

                FolderAutoRenameCompletionReceipt? completion = null;
                viewModel.FolderAutoRenameWorkflow.CompletionPublished += receipt => completion = receipt;
                FolderAutoRenameFailure? outcome = null;
                viewModel.FolderAutoRenameWorkflow.FailurePublished += failure => outcome = failure;
                if (allFolders)
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.FolderAutoRenameWorkflow.RequestStartAllAsync(root), "B1 all admission");
                }
                else
                {
                    Assert.IsTrue(viewModel.FolderAutoRenameWorkflow.RequestStartSelected([
                        new ChartOperationTarget((chart), null,
                            ChartOperationSourceScope.Library, true, false, false, ChartOperationCapabilities.MoveInLibrary)]));
                }

                TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.FolderAutoRenameWorkflow.WaitForIdleAsync(), "B1 terminal");
                Assert.IsTrue(activityInactive);
                Assert.IsTrue(leaseBusy, "必要な失敗通知中も論理受付を保持する。");
                using (LibraryFileMutationLease next = library.TryBeginLibraryFileMutation("after-report", showMessage: false))
                {
                    Assert.IsNotNull(next, "通知の実終端後は次の明示変更を受理する。");
                }
                Assert.AreEqual(1, dialogs.Messages.Count);
                Assert.AreEqual(0, dialogs.ModelMessages);
                Assert.AreEqual(MessageBoxImage.Error, dialogs.Messages[0].Icon);
                StringAssert.Contains(dialogs.Messages[0].MessageBoxText, "consumer-finalizer-marker");
                Assert.IsNull(completion);
                Assert.IsNotNull(outcome);
                Assert.IsTrue(outcome.HasDurableCommit);
                Assert.IsFalse(outcome.ExecutionResult.RefreshRequired);
                Assert.AreEqual(1, outcome.MutationResult.SessionReceipt.ConfirmedChangeCount);
                Assert.IsTrue(outcome.MutationResult.SessionReceipt.HasDurableFinalizationFailure);
                Assert.IsFalse(File.Exists(sourceChart));
                ChartFile current = library.BmsCharts.Single();
                Assert.AreEqual(sourceChart, chart.Path);
                Assert.IsTrue(File.Exists(current.Path));
                using LR2SongDBExtended verifyDb = new BmsLibraryDbGateway(dbPath).OpenSongDbReadOnly();
                Assert.IsNotNull(verifyDb.Find<LR2SongDB.song>(current.Path));
            }
            finally
            {
                if (viewModel != null)
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync(), "B1 close");
                    viewModel.SettingDialog.Dispose();
                }
                Directory.Delete(root, true);
            }
        });
    }
    /// <summary>外部ドロップの実導入で、成功・原本後片付け失敗・DB反映失敗をpackage件数と区別し、receiptと通知に確定済み変更と失敗を保持します。</summary>
    [DataTestMethod]
    [DataRow(0)] // 導入・原本の後片付けとも成功。
    [DataRow(1)] // 導入を確定した後の原本削除が失敗。
    [DataRow(2)] // 導入先songへのDB反映が失敗。
    public void DropInstallReportsRealReceiptIndependentlyOfPackageCount(int failureKind)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string root = Path.Combine(Path.GetTempPath(), "FSDB-B-drop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            MainWindowViewModel? viewModel = null;
            try
            {
                string source = Path.Combine(root, "DropSource");
                Directory.CreateDirectory(source);
                string sourceChart = Path.Combine(source, "chart.bms");
                File.WriteAllText(sourceChart, "#PLAYER 1\r\n#TITLE DropTarget\r\n#ARTIST Artist\r\n");
                string installRoot = Path.Combine(root, "Installed");
                Directory.CreateDirectory(installRoot);
                string destination = Path.Combine(installRoot, "DropTarget");
                string dbPath = Path.Combine(root, "song.db");
                using (var db = new LR2SongDBExtended(dbPath))
                {
                    db.CreateTable<LR2SongDB.song>();
                    db.CreateTable<LR2SongDB.folder>();
                    db.CreateTable<LR2SongDBExtended.maintenance>();
                    if (failureKind == 2)
                    {
                        db.Execute("CREATE TRIGGER fail_drop BEFORE INSERT ON song WHEN NEW.path LIKE '%DropTarget%' BEGIN SELECT RAISE(ABORT, 'drop-primary-marker'); END;");
                    }
                }
                var dialogs = new FileDbReportRecordingDialogs();
                IFileMutationService files = failureKind == 1
                    ? new BmsLibraryPackageInstallServiceTests.FailingDestinationDeleteFileMutationService(source)
                    : new ResilientFileMutationService();
                // 登録済みの原本は導入対象から除外されるため、ここでは導入先だけを登録します。
                var composition = new ApplicationComposition(
                    settingsEditSession: new NoOpSettingsEditSession(MainWindowViewModelTestFactory.CreateIsolatedSettings()),
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(),
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    fileDbMutationDialogService: dialogs);
                var library = new TestBmsLibrary(dbPath, null, null, files, dialogs,
                    new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    () => new BmsLibraryOptionsSnapshot
                    {
                        OperationModeLR2DB = false,
                        FolderNameFormat = "%TITLE%",
                        BMSInstallDir = installRoot,
                        KeepInstallablePackagesPending = false
                    }, operationAdmission: composition.OperationAdmission,
                    playlistOperationAdmission: composition.PlaylistOperationAdmission, settings: TestBmsFactory.CreateRootSettings([installRoot]))
                { BmsCharts = [] };
                viewModel = composition.CreateMainWindowViewModelForTest();
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                ((IStartupLibraryApplicationPort)viewModel).AttachStartupLibrary(library);
                PackageInstallCompletionReceipt? outcome = null;
                var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                viewModel.PackageInstallWorkflow.CompletionPublished += receipt => { outcome = receipt; completed.TrySetResult(true); };
                viewModel.PackageInstallWorkflow.FailurePublished += failure => completed.TrySetException(failure.Exception);
                bool inactiveAtReport = false;
                dialogs.OnMessage = () => inactiveAtReport = !viewModel.ChartMutationActivity.IsActive;
                viewModel.PackageInstallWorkflow.Enqueue([source]);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.PackageInstallWorkflow.WaitForIdleAsync(), "B2 idle");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(completed.Task, "B2 completion");
                Assert.IsNotNull(outcome);
                Assert.AreEqual(failureKind == 2 ? 0 : 1, outcome.Packages.Count);
                Assert.AreEqual(failureKind == 0 ? 0 : 1, dialogs.Messages.Count);
                Assert.AreEqual(0, dialogs.ModelMessages);
                if (failureKind != 0)
                {
                    Assert.IsTrue(inactiveAtReport);
                    Assert.AreEqual(failureKind == 1 ? MessageBoxImage.Warning : MessageBoxImage.Error, dialogs.Messages[0].Icon);
                    Assert.AreEqual(failureKind == 1, outcome.HasDurableCommit);
                    Assert.IsFalse(outcome.ManualRecoveryRequired);
                    if (failureKind == 2)
                    {
                        Assert.IsNotNull(outcome.SessionReceipt.ApplyFailure);
                        Assert.IsNull(outcome.SessionReceipt.FinalizationFailure);
                    }
                    // 通知は要約し、元失敗の詳細は変更不能なreceiptと診断へ保持します。
                    StringAssert.Contains(outcome.SessionReceipt.PrimaryFailure.ToString(),
                        failureKind == 1 ? "injected-destination-delete-failure" : "drop-primary-marker");
                    StringAssert.Contains(dialogs.Messages[0].MessageBoxText, source);
                }
                using LR2SongDBExtended verifyDb = new BmsLibraryDbGateway(dbPath).OpenSongDbReadOnly();
                Assert.AreEqual(failureKind != 2, verifyDb.Find<LR2SongDB.song>(Path.Combine(destination, "chart.bms")) != null);
                Assert.AreEqual(failureKind != 0, Directory.Exists(source));
            }
            finally
            {
                if (viewModel != null)
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync(), "B2 close");
                    viewModel.SettingDialog.Dispose();
                }
                Directory.Delete(root, true);
            }
        });
    }

    /// <summary>実取り込みの初段成功と後段DB失敗を、実MainWindowのowner通知とreceiverへ通し、一回の集約表示に成功・未確定・回復情報を保持します。</summary>
    [TestMethod]
    public void DropInstallAutomaticContinuation_ReportsBothStagesThroughActualReceiver()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var dialogs = new FileDbReportRecordingDialogs();
            var composition = new ApplicationComposition(
                settingsEditSession: new NoOpSettingsEditSession(MainWindowViewModelTestFactory.CreateIsolatedSettings()),
                uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                applicationLifetime: TestApplicationContext.CreateLifetime(),
                cultureCatalog: TestApplicationContext.CreateCultureCatalog(), fileDbMutationDialogService: dialogs);
            var fixture = new PackageInstallWorkflowOwnerTests.AutomaticContinuationFixture(true, false,
                operationAdmission: composition.OperationAdmission, playlistOperationAdmission: composition.PlaylistOperationAdmission,
                uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher));
            MainWindowViewModel? viewModel = null;
            FileStream? cleanupLock = null;
            string? cleanupRoot = null;
            string managedZipRoot = TempDirectoryPublisher.Get("automatic-receiver-input");
            string managedZip = Path.Combine(managedZipRoot, "no-charts.zip");
            using (ZipArchive zip = System.IO.Compression.ZipFile.Open(managedZip, System.IO.Compression.ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("readme.txt").Open())) { writer.Write("no charts"); }
            fixture.Files.BeforeTimestamp = path =>
            {
                if (Path.GetFileName(path) != "readme.txt" || cleanupLock != null) { return; }
                cleanupRoot = Path.GetDirectoryName(path) ?? throw new AssertFailedException("展開先がありません。");
                cleanupLock = new FileStream(Path.Combine(cleanupRoot, "cleanup-lock.marker"), FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            };
            try
            {
                string normal = fixture.WriteInput("Normal", "normal.bms", "normal");
                File.WriteAllText(Path.Combine(normal, "normal.bms"), BmsLibraryInitializationTestSupport.CreateValidBmsText("NewSong"));
                File.WriteAllBytes(Path.Combine(normal, "sound.wav"), [1]);
                string first = fixture.WriteInput("A", "a.bms", "a");
                string second = fixture.WriteInput("B", "b.bms", "b");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.InitializeAsync(), "automatic-report.initialize");
                fixture.Options = fixture.CreateOptions(true, false, keepNewPending: false);
                BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(fixture.Database, db => db.Execute(
                    "CREATE TRIGGER fail_actual_automatic_receiver BEFORE INSERT ON song WHEN NEW.path = '"
                    + Path.Combine(fixture.Destination, "b.bms").Replace("'", "''")
                    + "' BEGIN SELECT RAISE(ABORT, 'automatic-receiver-marker'); END;"));
                viewModel = composition.CreateMainWindowViewModelForTest();
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                ((IStartupLibraryApplicationPort)viewModel).AttachStartupLibrary(fixture.Library);
                PackageInstallCommandResult? outcome = null;
                viewModel.PackageInstallWorkflow.CompletionPublished += receipt => outcome = receipt.CommandResult;
                viewModel.PackageInstallWorkflow.FailurePublished += failure => outcome = failure.CommandResult;
                var displayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                bool activeAtReport = true;
                dialogs.OnMessage = () =>
                {
                    activeAtReport = composition.OperationAdmission.IsActive;
                    displayed.TrySetResult();
                };
                Assert.IsTrue(viewModel.PackageInstallWorkflow.Enqueue([normal, first, second, managedZip]));
                Task idle = viewModel.PackageInstallWorkflow.WaitForIdleAsync();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(idle, "automatic-report.idle");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(displayed.Task, "automatic-report.displayed");
                Assert.IsFalse(activeAtReport);
                Assert.IsNotNull(outcome);
                Assert.IsTrue(outcome.SessionReceipt.DurableCommit);
                Assert.IsNotNull(outcome.EstimatedInstallReceipt);
                Assert.IsFalse(outcome.EstimatedInstallReceipt.DurableCommit);
                Assert.AreEqual(1, outcome.RegisteredPackages.Count);
                Assert.IsNotNull(cleanupRoot);
                Assert.AreEqual(cleanupRoot, outcome.InputCleanupFailures.Single().Path);
                Assert.IsTrue(Directory.Exists(cleanupRoot));
                Assert.IsFalse(File.Exists(managedZip));
                using (LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly())
                {
                    Assert.IsNotNull(db.Table<LR2SongDB.song>().Single(row => row.path.EndsWith("normal.bms", StringComparison.OrdinalIgnoreCase)));
                }
                Assert.IsTrue(fixture.Library.BmsCharts.Any(chart => chart.Path.EndsWith("normal.bms", StringComparison.OrdinalIgnoreCase)));
                Assert.AreEqual(1, dialogs.Messages.Count);
                Assert.AreEqual(MessageBoxImage.Error, dialogs.Messages.Single().Icon);
                string body = dialogs.Messages.Single().MessageBoxText;
                StringAssert.Contains(body, "automatic-receiver-marker");
                StringAssert.Contains(body, fixture.Destination);
                StringAssert.Contains(body, cleanupRoot);
                StringAssert.Contains(body, outcome.InputCleanupFailures.Single().Failure.Message);
                StringAssert.Contains(body, string.Format(Resources.LibraryMutationSessionReport_Counts,
                    outcome.SessionReceipt.ConfirmedChangeCount + outcome.EstimatedInstallReceipt.ConfirmedChangeCount,
                    outcome.SessionReceipt.ConfirmedChangeCount, outcome.EstimatedInstallReceipt.ConfirmedChangeCount,
                    1, 1, 0));
                cleanupLock?.Dispose();
                cleanupLock = null;
                TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.PackageInstallWorkflow.WaitForIdleAsync(), "automatic-report.no-retry");
                Assert.IsTrue(Directory.Exists(cleanupRoot), "handle解放だけでは回収を自動再試行しません。");
            }
            finally
            {
                cleanupLock?.Dispose();
                if (viewModel != null)
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync(), "automatic-report.close");
                    viewModel.SettingDialog.Dispose();
                }
                TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.DisposeAsync().AsTask(), "automatic-report.fixture");
                if (cleanupRoot != null) { TempDirectoryPublisher.TryDeleteManagedPath(cleanupRoot); }
                TempDirectoryPublisher.TryDeleteManagedPath(managedZipRoot);
            }
        });
    }

    /// <summary>起動時の通常ルート選択後にライブラリを接続し、初期一覧と実Bindingを確認します。フォルダ統合の差分通知と後続の資源保守では、同じ行と表示元を保って不足WAVの表示と健康度索引を更新することを確認します。</summary>
    [TestMethod]
    public void ResourceHealthMaintenanceNotificationRefreshesRealizedNormalRowBeforeDisplay()
    {
        string root = Path.Combine(Path.GetTempPath(), "resource-health-wpf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
        {
            values.OperationModeLR2DB = false;
            values.StartupSelectInstallPending = false;
        });
        var rootRequests = new List<RegularChartTreeNavigationPresentationRequestedEventArgs>();
        var rootArrived = new TaskCompletionSource<RegularChartTreeNavigationPresentationRequestedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<RegularChartTreeNavigationPresentationRequestedEventArgs> rootRequested = (_, args) =>
        { rootRequests.Add(args); rootArrived.TrySetResult(args); };
        MainWindowViewModel? prepared = null;
        try
        {
            MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(settings, (scope, viewModel, window) =>
            {
                var started = new List<Task>();
                FieldInfo drainField = typeof(RegularChartListOwner).GetField("normalLibraryRefreshDrainCompletion", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new AssertFailedException("実通知drainの窓口がありません。");
                FieldInfo syncField = typeof(RegularChartListOwner).GetField("syncRoot", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new AssertFailedException("実準備Taskの同期境界がありません。");
                FieldInfo preparationsField = typeof(RegularChartListOwner).GetField("viewPreparationTasks", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new AssertFailedException("実準備Task集合がありません。");
                Task[] SnapshotPreparations()
                {
                    object synchronization = syncField.GetValue(viewModel.RegularChartList) ?? throw new AssertFailedException();
                    lock (synchronization)
                    {
                        if (preparationsField.GetValue(viewModel.RegularChartList) is not HashSet<Task> preparations) { throw new AssertFailedException(); }
                        return preparations.ToArray();
                    }
                }
                void AwaitDrain(string stage)
                {
                    Task drain = drainField.GetValue(viewModel.RegularChartList) as Task ?? throw new AssertFailedException();
                    started.Add(drain);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(drain, stage + "-notification-drain");
                }
                void AwaitTasks(IEnumerable<Task> tasks, string stage)
                {
                    var terminal = Task.WhenAll(tasks); started.Add(terminal);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(terminal, stage);
                }
                TestUiDispatcherHost.AwaitTaskOnDispatcher(rootArrived.Task, "constructor Root actual typed arrival");
                started.AddRange(rootRequests.Select(request => request.Presentation));
                Assert.AreEqual(1, rootRequests.Count, "実constructorの通常Rootを修復再選択せず捕捉します。");
                RegularChartTreeNavigationPresentationRequestedEventArgs initialRoot = rootRequests.Single();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(initialRoot.Presentation, "constructor Root actual Presentation terminal");
                Assert.AreEqual(MainViewUpdateMode.FolderFilterSelected, initialRoot.RefreshMode);
                Assert.IsNull(viewModel.RegularChartList.CaptureTreeFilter(enabled: true));
                AwaitTasks(SnapshotPreparations(), "constructor Root registered parent terminals");
                MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
                var table = (CustomTableView)window.FindName("customTableView");

                string sourceDirectory = Path.Combine(root, "resource-health-source");
                string destinationDirectory = Path.Combine(root, "resource-health-destination");
                Directory.CreateDirectory(sourceDirectory); Directory.CreateDirectory(destinationDirectory);
                string chartPath = Path.Combine(sourceDirectory, "resource-health.bms");
                string destinationPath = Path.Combine(destinationDirectory, "resource-health.bms");
                File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Resource health UI\r\n#WAV01 missing.wav\r\n#00111:01\r\n");
                ChartFile chart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
                ResourceHealthMaintenanceSnapshot initialMaintenance = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(chart);
                chart = ChartFileProjection.WithMaintenance(chart, initialMaintenance);
                string songDbPath = Path.Combine(root, "song.db");
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.CreateTable<LR2SongDB.song>(); songDb.CreateTable<LR2SongDB.folder>();
                    songDb.CreateTable<LR2SongDBExtended.maintenance>(); songDb.CreateTable<LR2SongDBExtended.bmson_song>();
                }
                TestBmsLibrary library = MainWindowViewModelTestFactory.CreateLibrary(songDbPath, settings, viewModel);
                library.BmsCharts = [];
                // 通常ルートを受理した後にライブラリを接続する、実際の起動順を保つ。
                ((IStartupLibraryApplicationPort)viewModel).AttachStartupLibrary(library);
                Task attachDelivery = viewModel.RegularChartList.DeliverPendingLibraryChangesAsync(library, CancellationToken.None);
                started.Add(attachDelivery);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(attachDelivery, "empty library attach actual delivery");
                AwaitTasks(SnapshotPreparations(), "empty attach accepted parent terminals");

                int sourcePhase = 1;
                HashSet<Task> prior = SnapshotPreparations().ToHashSet();
                var initialSourceTasks = new List<Task>();
                bool initialSourceObserved = false;
                bool mergeSourceObserved = false;
                bool initialRowsApplied = false;
                bool presentationRefreshObserved = false;
                IList? rowsAfterSourceRefresh = null;
                IList? rowsAfterPresentationRefresh = null;
                var initialBinding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var maintenanceTasks = new List<Task>();
                void ObserveRowsAndBinding()
                {
                    // 初期通知では一覧の置換とBindingを観測し、統合の差分通知では同じ行への更新を観測する。
                    if (sourcePhase != 1) { return; }
                    IList rows = viewModel.MainChartList.Rows;
                    bool matches = rows.Count == 1 && rows[0] is LibraryChartRow row && row.path == chartPath
                        && viewModel.MainChartList.LastAppliedColumnMode == MainViewUpdateMode.FolderFilterSelected;
                    if (!matches) { return; }
                    initialRowsApplied = true;
                    if (ReferenceEquals(table.ItemsSource, rows)) { initialBinding.TrySetResult(); }
                }
                PropertyChangedEventHandler rowsChanged = (_, args) =>
                { if (args.PropertyName == nameof(MainChartListViewModel.Rows)) { ObserveRowsAndBinding(); } };
                var itemsSource = DependencyPropertyDescriptor.FromProperty(CustomTableView.ItemsSourceProperty, typeof(CustomTableView));
                EventHandler bindingChanged = (_, _) => ObserveRowsAndBinding();
                EventHandler<NormalLibraryRefreshAppliedEventArgs> refreshApplied = (_, args) =>
                {
                    if (args.NotificationBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged))
                    {
                        if (sourcePhase == 1)
                        {
                            Task[] captured = SnapshotPreparations().Where(task => !prior.Contains(task)).ToArray();
                            started.AddRange(captured);
                            // 登録されるTaskの分割数は契約にせず、開始済み処理をすべて終端確認へ渡す。
                            initialSourceTasks.AddRange(captured);
                            initialSourceObserved = true;
                        }
                        else
                        {
                            // 統合の差分通知は同期的な購読処理が既存の通常一覧へ適用する。
                            mergeSourceObserved = true;
                        }
                    }
                    else if (sourcePhase == 2
                        && (args.NotificationBatch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged)
                            || args.NotificationBatch.HasEffect(LibraryChartRefreshEffects.MaintenancePresentationChanged)))
                    {
                        presentationRefreshObserved = true;
                        Task[] captured = SnapshotPreparations().Where(task => !prior.Contains(task)).ToArray();
                        maintenanceTasks.AddRange(captured); started.AddRange(captured);
                    }
                };
                void AwaitSource(string stage)
                {
                    AwaitDrain(stage);
                    if (sourcePhase == 1)
                    {
                        Assert.IsTrue(initialSourceObserved, "初期SourceChangedを実際の購読処理へ配送します。");
                        AwaitTasks(initialSourceTasks.Distinct(), stage + "-actual-source-task-terminals");
                        // 空のTask集合では表示成功とせず、期待Rowsが実表のBindingへ届くことを必須にする。
                        TestUiDispatcherHost.AwaitPresentationOnDispatcher(initialBinding.Task, stage + "-actual-Rows-Binding");
                        AwaitTasks(SnapshotPreparations(), stage + "-visible-source-preparation-terminals");
                        Assert.IsTrue(initialRowsApplied, "初期SourceChangedによる期待Rowsの実公開を確認します。");
                    }
                    else
                    {
                        // 統合の入力から差分配送と判断し、同じUI配送とその購読処理の完了まで確認する。
                        Assert.IsTrue(mergeSourceObserved, "実mergeのSource consumerを終端まで配送します。");
                    }
                    Assert.AreEqual(1, viewModel.MainChartList.Rows.Count);
                    string expectedPath = sourcePhase == 1 ? chartPath : destinationPath;
                    CollectionAssert.AreEqual(new[] { expectedPath }, viewModel.MainChartList.Rows.Cast<LibraryChartRow>().Select(row => row.path).ToArray());
                    Assert.AreEqual(MainViewUpdateMode.FolderFilterSelected, viewModel.MainChartList.LastAppliedColumnMode);
                    Assert.IsNull(viewModel.RegularChartList.CaptureTreeFilter(enabled: true));
                    // 差分通知の処理完了後も行とItemsSourceの対応を保ち、更新された値を直接確認する。
                    Assert.AreSame(viewModel.MainChartList.Rows, table.ItemsSource);
                }
                System.Runtime.ExceptionServices.ExceptionDispatchInfo? sourceHookFailure = null;
                // 統合は全利用権の解放後に通知を公開する。後続の資源保守を始める前に、ライブラリ接続時の表示を確定させる。
                PropertyChangedEventHandler flushSourceRefresh = (_, args) =>
                {
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion)
                        && sourcePhase == 2 && rowsAfterSourceRefresh == null && sourceHookFailure == null)
                    {
                        try { AwaitSource("merge-source"); rowsAfterSourceRefresh = viewModel.MainChartList.Rows; }
                        catch (Exception exception) { sourceHookFailure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception); throw; }
                    }
                };
                viewModel.MainChartList.PropertyChanged += rowsChanged;
                itemsSource.AddValueChanged(table, bindingChanged);
                viewModel.RegularChartList.NormalLibraryRefreshApplied += refreshApplied;
                library.PropertyChanged += flushSourceRefresh;
                Exception? failure = null;
                try
                {
                    library.BmsCharts = [chart];
                    AwaitSource("initial-source");
                    Assert.AreEqual(1, library.BmsCharts.Count);
                    Assert.IsTrue(library.NormalLibraryRefreshNotificationVersion > 0);
                    LibraryChartRow row = viewModel.MainChartList.Rows.Cast<LibraryChartRow>().Single();
                    StringAssert.Contains(row.WarningDigestText, Resources.WarningDigest_ResourceMissing);
                    StringAssert.Contains(row.WarningTooltipText, "WAV");
                    sourcePhase = 2;
                    prior = SnapshotPreparations().ToHashSet();
                    DuplicateMergeMaintenanceReceipt merge = library.MergeChartDirectory(sourceDirectory, destinationDirectory, operationId: 1);
                    sourceHookFailure?.Throw();
                    Assert.IsTrue(merge.MergeApplied, merge.SessionReceipt.PrimaryFailure?.ToString());
                    Assert.IsTrue(merge.ResourceHealthIndexDeferred);
                    AwaitDrain("maintenance-presentation");
                    AwaitTasks(maintenanceTasks, "maintenance actual registered terminal");
                    rowsAfterPresentationRefresh = viewModel.MainChartList.Rows;
                    Assert.IsNotNull(rowsAfterSourceRefresh);
                    Assert.IsTrue(presentationRefreshObserved);
                    Assert.AreSame(rowsAfterSourceRefresh, rowsAfterPresentationRefresh);
                    Assert.AreSame(rowsAfterSourceRefresh, viewModel.MainChartList.Rows);
                    Assert.AreSame(viewModel.MainChartList.Rows, table.ItemsSource);
                    Assert.AreEqual(1, library.BmsCharts.Count);
                    Assert.AreEqual(1, viewModel.MainChartList.Rows.Count);
                    LibraryChartRow refreshedRow = viewModel.MainChartList.Rows.Cast<LibraryChartRow>().Single();
                    StringAssert.Contains(refreshedRow.WarningDigestText, Resources.WarningDigest_ResourceMissing);
                    StringAssert.Contains(refreshedRow.WarningTooltipText, "WAV");
                    ResourceHealthIndexSnapshot snapshot = library.TryGetCurrentResourceHealthIndexSnapshotForView();
                    Assert.AreNotSame(ResourceHealthIndexSnapshot.Empty, snapshot);
                    ChartFile installed = library.BmsCharts.Single();
                    Assert.IsTrue(snapshot.GetProjection(ChartFileKind.Bms, installed.Path, installed.Md5).Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
                }
                catch (Exception exception) { failure = exception; throw; }
                finally
                {
                    try
                    {
                        if (failure != null) { started.Add(viewModel.RegularChartList.StopAsync()); }
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(started), "resource-health-started-task-cleanup");
                    }
                    catch (Exception cleanupFailure) when (failure != null) { failure.Data["ResourceHealthPresentationCleanupFailure"] = cleanupFailure; }
                    finally
                    {
                        library.PropertyChanged -= flushSourceRefresh;
                        viewModel.RegularChartList.NormalLibraryRefreshApplied -= refreshApplied;
                        viewModel.MainChartList.PropertyChanged -= rowsChanged;
                        itemsSource.RemoveValueChanged(table, bindingChanged);
                    }
                }
            }, prepareViewModel: viewModel =>
            {
                prepared = viewModel;
                viewModel.RegularChartList.TreeNavigationPresentationRequested += rootRequested;
            });
        }
        finally
        {
            if (prepared != null) { TestUiDispatcherHost.Invoke(() => prepared.RegularChartList.TreeNavigationPresentationRequested -= rootRequested); }
            Directory.Delete(root, true);
        }
    }

    /// <summary>全走査の仮想一覧で導入先候補を検索・解除し、行と選択を保持して必要行だけを実体化し、次の要求へ更新済み候補を渡します。</summary>
    [TestMethod]
    public void CorrectInstallDestinationSearchAndClearPreserveFullScanPresentation()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_CorrectPresentation_" + Guid.NewGuid().ToString("N"));
        string sourceDirectory = Path.Combine(root, "000Source");
        string candidateADirectory = Path.Combine(root, "InstalledA");
        string candidateBDirectory = Path.Combine(root, "InstalledB");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(candidateADirectory);
        Directory.CreateDirectory(candidateBDirectory);
        string sourcePath = Path.Combine(sourceDirectory, "source.bms");
        string candidateAPath = Path.Combine(candidateADirectory, "installed-a.bms");
        string candidateBPath = Path.Combine(candidateBDirectory, "installed-b.bms");
        const string chartText = "#PLAYER 1\r\n#TITLE A Repair Target\r\n#ARTIST Repair Artist\r\n#WAVAA sound.wav\r\n#00111:AA\r\n";
        File.WriteAllText(sourcePath, chartText);
        File.WriteAllText(candidateAPath, chartText);
        File.WriteAllText(candidateBPath, chartText);
        File.WriteAllText(Path.Combine(candidateADirectory, "sound.wav"), "candidate");
        File.WriteAllText(Path.Combine(candidateBDirectory, "sound.wav"), "candidate");
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.StartupSelectInstallPending = false;
            });
        MainWindowViewModel? preparedViewModel = null;
        Task<bool>? navigationTask = null;
        var maintenanceTreeTerminal = new MainWindowMaintenanceTreeTerminal(
            (mode, parameter) =>
            {
                navigationTask = preparedViewModel!.RegularChartList.NavigateMaintenanceAsync(mode, parameter);
                return navigationTask;
            });
        var options = new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false, ScanBmsFilesOnStartup = true };
        var scanner = CapturedChartFileScanner.FromFixture(
            [sourcePath, candidateAPath, candidateBPath],
            new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [sourceDirectory] = [],
                [candidateADirectory] = ["sound.wav"],
                [candidateBDirectory] = ["sound.wav"]
            },
            [root]);
        TestBmsLibrary? initializedLibrary = null;
        try
        {
            MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
                settings,
                (viewModel, window) =>
                {
                    var rowsApplied = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    viewModel.MainChartList.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(MainChartListViewModel.Rows))
                        {
                            rowsApplied.TrySetResult(null);
                        }
                    };
                    var fullScanItem = (TreeViewItem)window.FindName("treeViewItemFullScanCheckAllCharts")!;
                    fullScanItem.RaiseEvent(new RoutedEventArgs(TreeViewItem.SelectedEvent, fullScanItem));
                    Assert.IsNotNull(navigationTask);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(navigationTask!, "full-scan all charts navigation");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(rowsApplied.Task, "full-scan all charts rows");
                    rowsApplied = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    viewModel.MainChartList.RequestSort(nameof(LibraryChartRow.path), System.ComponentModel.ListSortDirection.Ascending);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(rowsApplied.Task, "full-scan path sort");

                    Assert.IsInstanceOfType(viewModel.MainChartList.Rows, typeof(ChartListVirtualView));
                    var rows = (ChartListVirtualView)viewModel.MainChartList.Rows;
                    Assert.AreEqual(3, rows.Count);
                    Assert.AreEqual(0, rows.RealizedRowCount);
                    var realizedRow = (LibraryChartRow)rows[0];
                    Assert.AreEqual(sourcePath, realizedRow.path);
                    Assert.AreEqual(1, rows.RealizedRowCount);
                    viewModel.MainChartList.SelectedIndex = 0;
                    Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(realizedRow, out ChartOperationTarget target));
                    Assert.AreEqual(ChartOperationSourceScope.Library, target.SourceScope);
                    Assert.IsFalse(target.IsPending);
                    Assert.IsTrue(RepairInstalledLocationRequest.TryCreate([target], out RepairInstalledLocationRequest searchRequest));

                    var displayApplied = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    PendingPackageMutationAppliedEventArgs? searchCompletion = null;
                    viewModel.MainChartList.DisplayRefreshRequested += (_, _) => displayApplied.TrySetResult(null);
                    viewModel.PendingPackages.WorkflowChanged += (_, args) =>
                    {
                        if (args is PendingPackageMutationAppliedEventArgs mutation)
                        {
                            searchCompletion = mutation;
                        }
                    };
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        viewModel.PendingPackages.SearchCorrectAsync(searchRequest),
                        "correct install destination search");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(displayApplied.Task, "correct search display refresh");

                    Assert.IsNotNull(searchCompletion);
                    Assert.AreEqual(1, searchCompletion!.ChangedCharts.Count);
                    Assert.IsTrue(searchCompletion.InstallDestinationStateChanged);
                    Assert.IsTrue(searchCompletion.ChangedCharts.All(chart =>
                        string.Equals(chart.Path, target.Chart.Path, StringComparison.OrdinalIgnoreCase)));
                    Assert.AreSame(rows, viewModel.MainChartList.Rows);
                    Assert.AreEqual(0, viewModel.MainChartList.SelectedIndex);
                    Assert.AreEqual(1, rows.RealizedRowCount);
                    Assert.AreEqual(string.Empty, realizedRow.instl_dst);
                    Assert.AreEqual("A Repair Target", realizedRow.InstallDestinationTitle);
                    Assert.AreEqual("Repair Artist", realizedRow.InstallDestinationArtist);
                    CollectionAssert.AreEquivalent(
                        new[] { candidateADirectory, candidateBDirectory },
                        realizedRow.Chart.InstallDestinationSuggestions.ToArray());
                    Assert.IsTrue(realizedRow.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));

                    displayApplied = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    PendingPackageMutationAppliedEventArgs? clearCompletion = null;
                    LibraryChartRow rowBeforeClear = realizedRow;
                    Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(realizedRow, out ChartOperationTarget clearTarget));
                    Assert.IsTrue(RepairInstalledLocationRequest.TryCreate([clearTarget], out RepairInstalledLocationRequest clearRequest));
                    viewModel.PendingPackages.WorkflowChanged += (_, args) =>
                    {
                        if (args is PendingPackageMutationAppliedEventArgs mutation)
                        {
                            clearCompletion = mutation;
                        }
                    };
                    viewModel.MainChartList.DisplayRefreshRequested += (_, _) => displayApplied.TrySetResult(null);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        viewModel.PendingPackages.ClearCorrectAsync(clearRequest),
                        "correct install destination clear");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(displayApplied.Task, "correct clear display refresh");

                    Assert.IsNotNull(clearCompletion);
                    Assert.AreEqual(1, clearCompletion!.ChangedCharts.Count);
                    Assert.IsTrue(clearCompletion.InstallDestinationStateChanged);
                    Assert.AreSame(rows, viewModel.MainChartList.Rows);
                    Assert.AreEqual(0, viewModel.MainChartList.SelectedIndex);
                    Assert.AreEqual(1, rows.RealizedRowCount);
                    Assert.AreSame(rowBeforeClear, rows[0]);
                    Assert.AreEqual(string.Empty, realizedRow.instl_dst);
                    Assert.AreEqual(string.Empty, realizedRow.InstallDestinationTitle);
                    Assert.AreEqual(string.Empty, realizedRow.InstallDestinationArtist);
                    Assert.AreEqual(0, realizedRow.Chart.InstallDestinationSuggestions.Count);
                    Assert.IsFalse(realizedRow.Chart.Warnings.Any(warning => warning.Category == ChartWarningCategory.InstallEstimation));
                    Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(realizedRow, out ChartOperationTarget nextTarget));
                    Assert.IsTrue(RepairInstalledLocationRequest.TryCreate([nextTarget], out RepairInstalledLocationRequest nextRequest));
                    Assert.IsFalse(nextRequest.HasInstallDestination);
                    Assert.AreEqual(0, nextRequest.RepairCharts.Single().InstallDestinationSuggestions.Count);
                },
                maintenanceTreeTerminal: maintenanceTreeTerminal,
                prepareViewModel: viewModel =>
                {
                    preparedViewModel = viewModel;
                    ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(viewModel);
                    var library = new TestBmsLibrary(
                        songDbPath,
                        getLR2Config: null,
                        _lr2ScoreDB: null,
                        startupRequiredFileScanReason: null,
                        optionsSnapshotProvider: () => options,
                        applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
                        chartFileScanner: scanner,
                        operationAdmission: composition.OperationAdmission,
                        playlistOperationAdmission: composition.PlaylistOperationAdmission, settings: TestBmsFactory.CreateRootSettings([root]))
                    {

                        StartupBackgroundTaskScheduler = (_, _, _, _) => false
                    };
                    initializedLibrary = library;
                    library.Initialize(null, null, BMSLibrary.LibraryInitializeMode.Startup);
                    ((IStartupLibraryApplicationPort)viewModel).AttachStartupLibrary(library);
                });
        }
        finally
        {
            initializedLibrary?.RequestShutdown("correct-install-destination-presentation-test");
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>実推定のRelated候補が改名・別root移動後も生成済み行から選べ、根拠・警告・健康度を保ち、明示クリアでは登録時の結果へ戻ることを確認します。</summary>
    /// <remarks>分類条件表は下位へ分担し、compiled推定・移動したchild候補と絶対drive rootのセル入力・再編集初期値を、各回の実受信Task・Presentation・L終端へ接続します。</remarks>
    [TestMethod]
    public void PendingEstimationAndCandidateEditingReachRealizedRowsWithRelatedEvidence()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_PendingPresentation_" + Guid.NewGuid().ToString("N"));
        OwnedTestDrive? drive = null;
        TestBmsLibrary? library = null;
        Exception? primaryFailure = null;
        try
        {
            drive = OwnedTestDrive.Create(root);
            drive.AssertOwned();
            string absoluteRoot = drive.Root[0] + @":\";
            string registeredRoot = Path.Combine(absoluteRoot, "LibraryRoot");
            string candidate = Path.Combine(registeredRoot, "Candidate");
            string destinationRoot = Path.Combine(drive.Root[0] + @":\", "OtherRoot");
            string source = Path.Combine(root, "Pending");
            string otherSource = Path.Combine(root, "OtherPending");
            Directory.CreateDirectory(candidate);
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(otherSource);
            Directory.CreateDirectory(destinationRoot);
            string[] ownedPaths = [Path.Combine(candidate, "majority-a.bms"), Path.Combine(candidate, "majority-b.bms"), Path.Combine(candidate, "related.bms")];
            const string audio = "\r\n#WAV01 sound.wav\r\n#00111:01\r\n";
            File.WriteAllText(ownedPaths[0], "#TITLE Collection Main\r\n#ARTIST Collection Author" + audio);
            File.WriteAllText(ownedPaths[1], "#TITLE Collection Main\r\n#ARTIST Collection Author\r\n#GENRE second" + audio);
            File.WriteAllText(ownedPaths[2], "#TITLE Target Song\r\n#ARTIST Candidate Artist" + audio);
            string rootOwnedPath = Path.Combine(absoluteRoot, "root-owned.bms");
            File.WriteAllText(rootOwnedPath, "#TITLE Target Song\r\n#ARTIST Candidate Artist\r\n#GENRE root-only\r\n");
            File.WriteAllText(Path.Combine(candidate, "sound.wav"), "audio");
            string sourcePath = Path.Combine(source, "pending.bms");
            string otherPath = Path.Combine(otherSource, "other.bms");
            File.WriteAllText(sourcePath, "#TITLE Target Song\r\n#ARTIST Source Author" + audio);
            File.WriteAllText(otherPath, "#TITLE Other Pending\r\n#ARTIST Other Author\r\n");
            var entry = PackageChartEntry.FromPath(sourcePath);
            var otherEntry = PackageChartEntry.FromPath(otherPath);
            BmsLibraryPackageInstallService.InitializePendingResourceHealth(entry);
            BmsLibraryPackageInstallService.InitializePendingResourceHealth(otherEntry);
            var package = ChartPackage.FromChartEntries([entry]);
            package.path = source;
            var otherPackage = ChartPackage.FromChartEntries([otherEntry]);
            otherPackage.path = otherSource;
            string songDbPath = Path.Combine(root, "song.db");
            File.WriteAllBytes(songDbPath, []);
            Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values => values.StartupSelectInstallPending = false);
            var options = new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false, ScanBmsFilesOnStartup = true };
            var scanner = CapturedChartFileScanner.FromFixture(ownedPaths.Append(rootOwnedPath),
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [absoluteRoot] = [Path.GetRelativePath(absoluteRoot, Path.Combine(candidate, "sound.wav"))],
                    [registeredRoot] = [Path.GetRelativePath(registeredRoot, Path.Combine(candidate, "sound.wav"))],
                    [candidate] = ["sound.wav"],
                    [destinationRoot] = []
                }, [absoluteRoot, destinationRoot]);
            // root/containerの資源は子からの集約であり、直下の所有音源には数えません。
            ChartScanResult scan = scanner.Scan([absoluteRoot, destinationRoot], [".bms"]).Result;
            scan.SelfOwnedAudioRelativePathHashesByChartDirectory[absoluteRoot] = [];
            scan.SelfOwnedAudioRelativePathHashesByChartDirectory[registeredRoot] = [];
            MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(settings, (scope, viewModel, window) =>
            {
                TestBmsLibrary currentLibrary = library ?? throw new AssertFailedException("準備済みライブラリが必要です。");
                ChartFile rootOwnedBefore = currentLibrary.BmsCharts.Single(chart => chart.Path == rootOwnedPath);
                MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
                var rowsApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                viewModel.MainChartList.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(MainChartListViewModel.Rows)) { rowsApplied.TrySetResult(); }
                };
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    viewModel.RegularChartList.NavigateInstallAsync(MainViewUpdateMode.PendingInstallFolderSelected), "pending rows navigation");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(rowsApplied.Task, "pending rows applied");
                var table = (CustomTableView)window.FindName("customTableView");
                LibraryChartRow row = viewModel.MainChartList.Rows.Cast<LibraryChartRow>().Single(value => value.path == sourcePath);
                LibraryChartRow otherRow = viewModel.MainChartList.Rows.Cast<LibraryChartRow>().Single(value => value.path == otherPath);
                // 起動時の所持判定・表示投影を終えた現在値を、これから行う操作の比較基準にします。
                ChartFile otherBefore = otherEntry.Chart;
                int otherProjectionVersion = otherEntry.ProjectionVersion;
                Assert.AreEqual(string.Empty, row.InstallDestinationTitle);
                Assert.AreEqual(0, row.Chart.WAVHealth);
                var menu = (ContextMenu)window.FindResource("treeViewInstallPackageContextMenu");
                menu.PlacementTarget = new TreeViewItem { DataContext = package };
                MenuItem group = menu.Items.OfType<MenuItem>().Single(item => item.Items.OfType<MenuItem>().Any(child => Equals(child.Header, Resources.Estimate_install_loc)));
                MenuItem search = group.Items.OfType<MenuItem>().Single(item => Equals(item.Header, Resources.Estimate_install_loc));
                Task searchEvent = MainWindowPresentationTestHarness.CaptureAsyncEventCompletion(() => RaiseMenuClick(search));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(searchEvent, "real pending estimation menu");
                // ハンドラーは実推定Taskを待ちます。モデルの解除と活動終端を、表示行の寿命と分けて確認します。
                Assert.IsFalse(entry.Chart.Status.HasFlag(ChartFileStatus.SEARCHING));
                Assert.IsFalse(viewModel.ChartMutationActivity.IsActive);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                CaptureCurrentRows();
                AssertEvidence();
                Assert.AreEqual(string.Empty, row.instl_dst);
                CollectionAssert.AreEqual(new[] { candidate }, row.Chart.InstallDestinationSuggestions.ToArray());
                Assert.IsTrue(row.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));
                Assert.IsFalse(row.Chart.Status.HasFlag(ChartFileStatus.SEARCHING));

                (ChartWarningKind Kind, string Message)[] estimationWarnings = row.Chart.Warnings.Select(warning => (warning.Kind, warning.Message)).ToArray();
                (ChartWarningKind Kind, string Message)[] estimationReasons = row.Chart.Warnings
                    .Where(warning => warning.Category == ChartWarningCategory.InstallEstimation)
                    .Select(warning => (warning.Kind, warning.Message)).ToArray();
                string renamedCandidate = Path.Combine(registeredRoot, "Renamed");
                ChangeCandidate(() => currentLibrary.RenameChartFolder(candidate, "Renamed"), renamedCandidate);
                Assert.AreEqual(string.Empty, row.instl_dst);
                CollectionAssert.AreEqual(new[] { candidate }, row.Chart.InstallDestinationSuggestions.ToArray());
                CollectionAssert.AreEqual(estimationWarnings, row.Chart.Warnings.Select(warning => (warning.Kind, warning.Message)).ToArray());
                AssertEvidence();

                Assert.AreEqual(ChartOperationSourceScope.PendingPackage, viewModel.MainChartList.CurrentOperationContext.SourceScope);
                Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, viewModel.MainChartList.CurrentOperationContext.SourceScope, out ChartOperationTarget target));
                Assert.AreSame(entry, target.PackageEntry);
                Assert.IsTrue(PendingInstallDestinationEditRequest.TryCreate(target, out PendingInstallDestinationEditRequest edit));
                Assert.AreSame(viewModel.MainChartList.Rows, table.ItemsSource);
                table.Width = 900d;
                table.Height = 220d;
                table.HeaderHeight = 0d;
                table.RowHeight = 70d;
                // 通常一覧から継承した列設定でも、今回操作するcompiled列を表示してから現在セルを作ります。
                viewModel.MainChartList.ColumnsSettings.InstallDst.Visibility = Visibility.Visible;
                CustomTableColumn destinationColumn = table.Columns.Single(column => column.EditPropertyName == "instl_dst");
                table.Columns = [destinationColumn];
                CommitCompiledDestination(candidate);
                Assert.AreEqual(candidate, entry.Chart.InstallDestination);
                SelectDestinationCell();
                Assert.IsTrue(table.HandleKeyDown(Key.F2, ModifierKeys.None),
                    $"再編集のF2: current={table.GetCurrentCellColumn(table.SelectedIndex)?.EditPropertyName}, width={table.ActualWidth}, height={table.ActualHeight}, scope={viewModel.MainChartList.CurrentOperationContext.SourceScope}");
                Assert.AreEqual(candidate, FindCurrentEditors(table).Single().Text);
                Assert.IsTrue(table.HandleKeyDown(Key.Escape, ModifierKeys.None));
                table.HandleKeyDown(Key.Escape, ModifierKeys.None);
                Assert.AreEqual(0, FindCurrentEditors(table).Count());
                Assert.AreEqual(candidate, row.instl_dst);
                AssertEvidence();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.PendingPackages.SetPendingAsync(edit, string.Empty), "empty destination edit");
                Assert.AreEqual(string.Empty, row.instl_dst);
                Assert.AreEqual(string.Empty, row.InstallDestinationTitle);
                Assert.AreEqual(string.Empty, row.InstallDestinationArtist);
                CollectionAssert.AreEqual(new[] { candidate }, row.Chart.InstallDestinationSuggestions.ToArray());
                Assert.IsTrue(row.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));
                string movedCandidate = Path.Combine(destinationRoot, "Renamed");
                LibraryChartRef[] movingCharts = [.. currentLibrary.BmsCharts
                    .Where(chart => string.Equals(Path.GetDirectoryName(chart.Path), candidate, StringComparison.OrdinalIgnoreCase))
                    .Select(chart => LibraryChartRef.FromChartFile(chart))];
                Assert.AreEqual(3, movingCharts.Length);
                ChangeCandidate(() => currentLibrary.MoveLibraryRootFolder(movingCharts, destinationRoot), movedCandidate);
                Assert.AreEqual(string.Empty, row.instl_dst);
                CollectionAssert.AreEqual(new[] { candidate }, row.Chart.InstallDestinationSuggestions.ToArray());
                Assert.AreEqual(100, row.Chart.WAVHealth);
                CommitCompiledDestination(absoluteRoot);
                Assert.AreEqual(absoluteRoot, entry.Chart.InstallDestination);
                Assert.AreEqual(absoluteRoot, row.instl_dst);
                SelectDestinationCell();
                Assert.IsTrue(table.HandleKeyDown(Key.F2, ModifierKeys.None));
                Assert.AreEqual(absoluteRoot, FindCurrentEditors(table).Single().Text);
                Assert.IsTrue(table.HandleKeyDown(Key.Escape, ModifierKeys.None));
                table.HandleKeyDown(Key.Escape, ModifierKeys.None);
                Assert.AreEqual(0, FindCurrentEditors(table).Count());
                Assert.AreEqual(absoluteRoot, entry.Chart.InstallDestination);
                Assert.AreEqual(absoluteRoot, row.instl_dst);
                AssertOtherUnchanged();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.PendingPackages.SetPendingAsync(edit, candidate), "Related candidate reselection");
                CaptureCurrentRows();
                Assert.AreEqual(candidate, entry.Chart.InstallDestination);
                Assert.AreEqual(candidate, row.instl_dst);
                CollectionAssert.AreEqual(new[] { candidate }, entry.Chart.InstallDestinationSuggestions.ToArray());
                CollectionAssert.AreEqual(new[] { candidate }, row.Chart.InstallDestinationSuggestions.ToArray());
                // 推定理由のMessageは旧pathを含む診断として保ち、root編集時の現在健康度とは分けて比較します。
                CollectionAssert.AreEqual(estimationReasons, entry.Chart.Warnings
                    .Where(warning => warning.Category == ChartWarningCategory.InstallEstimation)
                    .Select(warning => (warning.Kind, warning.Message)).ToArray());
                CollectionAssert.AreEqual(estimationReasons, row.Chart.Warnings
                    .Where(warning => warning.Category == ChartWarningCategory.InstallEstimation)
                    .Select(warning => (warning.Kind, warning.Message)).ToArray());
                Assert.AreEqual(100, entry.Chart.WAVHealth);
                Assert.AreEqual(100, row.Chart.WAVHealth);
                Assert.IsFalse(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
                Assert.IsFalse(row.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
                AssertEvidence();
                MenuItem clear = group.Items.OfType<MenuItem>().Single(item => Equals(item.Header, Resources.Clear_estimation));
                File.WriteAllText(Path.Combine(source, "sound.wav"), "added after registration");
                Task clearEvent = MainWindowPresentationTestHarness.CaptureAsyncEventCompletion(() => RaiseMenuClick(clear));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(clearEvent, "real pending destination clear menu");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                CaptureCurrentRows();
                Assert.AreEqual(string.Empty, row.instl_dst);
                Assert.AreEqual(string.Empty, row.InstallDestinationTitle);
                Assert.AreEqual(string.Empty, row.InstallDestinationArtist);
                Assert.AreEqual(0, row.Chart.InstallDestinationSuggestions.Count);
                Assert.IsFalse(row.Chart.Warnings.Any(warning => warning.Category == ChartWarningCategory.InstallEstimation));
                Assert.AreEqual(0, row.Chart.WAVHealth);
                Assert.IsTrue(row.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
                StringAssert.Contains(row.WarningDigestText, Resources.WarningDigest_ResourceMissing);
                LibraryChartRow refreshed = viewModel.MainChartList.Rows.Cast<LibraryChartRow>().Single(value => value.path == sourcePath);
                Assert.AreEqual(0, refreshed.Chart.WAVHealth);
                StringAssert.Contains(refreshed.WarningDigestText, Resources.WarningDigest_ResourceMissing);
                AssertOtherUnchanged();
                Assert.AreEqual(string.Empty, otherRow.instl_dst);
                Assert.AreEqual(string.Empty, otherRow.InstallDestinationTitle);
                Assert.AreEqual(0, otherRow.Chart.InstallDestinationSuggestions.Count);

                void CommitCompiledDestination(string destinationText)
                {
                    SelectDestinationCell();
                    Assert.IsTrue(table.HandleKeyDown(Key.F2, ModifierKeys.None));
                    TextBox editor = FindCurrentEditors(table).Single();
                    editor.Text = destinationText;
                    ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(viewModel);
                    var mutation = new TaskCompletionSource<PendingPackageMutationAppliedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                    void OnMutation(object? sender, PendingPackageWorkflowChangedEventArgs args)
                    {
                        if (args is PendingPackageMutationAppliedEventArgs applied) { mutation.TrySetResult(applied); }
                    }
                    Task? admissionTerminal = null;
                    Exception? editFailure = null;
                    viewModel.PendingPackages.WorkflowChanged += OnMutation;
                    try
                    {
                        Assert.IsTrue(table.HandleKeyDown(Key.Return, ModifierKeys.None));
                        admissionTerminal = composition.OperationAdmission.WaitForIdleAsync();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(
                            TestUiDispatcherHost.AwaitNotificationAsync(mutation.Task, admissionTerminal, "compiled destination mutation"), "compiled destination mutation");
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(mutation.Task.GetAwaiter().GetResult().Presentation, "compiled destination presentation");
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(admissionTerminal, "compiled destination admission terminal");
                        Assert.IsFalse(composition.OperationAdmission.IsActive);
                    }
                    catch (Exception failure) { editFailure = failure; throw; }
                    finally
                    {
                        var cleanupFailures = new List<Exception>();
                        try
                        {
                            // 到達後に表明が失敗しても、実受信とL終端を別々に回収してからWindowを閉じます。
                            if (mutation.Task.IsCompletedSuccessfully)
                            { TestUiDispatcherHost.AwaitTaskOnDispatcher(mutation.Task.Result.Presentation, "compiled destination presentation cleanup"); }
                        }
                        catch (Exception failure) { cleanupFailures.Add(failure); }
                        try
                        {
                            if (admissionTerminal != null)
                            { TestUiDispatcherHost.AwaitTaskOnDispatcher(admissionTerminal, "compiled destination admission cleanup"); }
                        }
                        catch (Exception failure) { cleanupFailures.Add(failure); }
                        finally { viewModel.PendingPackages.WorkflowChanged -= OnMutation; }
                        if (cleanupFailures.Count != 0)
                        {
                            if (editFailure != null) { cleanupFailures.Insert(0, editFailure); }
                            throw new AggregateException(cleanupFailures);
                        }
                    }
                    CaptureCurrentRows();
                }

                void AssertEvidence()
                {
                    Assert.AreEqual("Target Song", row.InstallDestinationTitle);
                    Assert.AreEqual("Candidate Artist", row.InstallDestinationArtist);
                    AssertOtherUnchanged();
                }

                void ChangeCandidate(Action operation, string nextCandidate)
                {
                    var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    void OnRefresh(object? sender, NormalLibraryRefreshAppliedEventArgs args) => refreshed.TrySetResult();
                    viewModel.RegularChartList.NormalLibraryRefreshApplied += OnRefresh;
                    try
                    {
                        operation();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(refreshed.Task, "candidate relocation presentation");
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(MainWindowViewModelTestFactory.GetComposition(viewModel).OperationAdmission.WaitForIdleAsync(), "candidate relocation admission terminal");
                        candidate = nextCandidate;
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        CaptureCurrentRows();
                        Assert.IsTrue(File.Exists(rootOwnedPath));
                        ChartFile rootOwnedCurrent = currentLibrary.BmsCharts.Single(chart => chart.Path == rootOwnedPath);
                        Assert.AreEqual(rootOwnedPath, rootOwnedCurrent.Path);
                        Assert.AreSame(rootOwnedBefore.Token, rootOwnedCurrent.Token);
                        using LR2SongDBExtended readback = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
                        Assert.AreEqual(rootOwnedPath, readback.Table<LR2SongDB.song>().Single(chart => chart.path == rootOwnedPath).path);
                    }
                    finally { viewModel.RegularChartList.NormalLibraryRefreshApplied -= OnRefresh; }
                }

                void CaptureCurrentRows()
                {
                    // 推定・クリアの表示再構築で退役した行へ、後続のキャッシュ更新を要求しません。
                    Assert.AreSame(viewModel.MainChartList.Rows, table.ItemsSource);
                    row = table.ItemsSource.Cast<LibraryChartRow>().Single(value => value.path == sourcePath);
                    otherRow = table.ItemsSource.Cast<LibraryChartRow>().Single(value => value.path == otherPath);
                }

                void SelectDestinationCell()
                {
                    // 非同期受信中の親layoutから戻った後も、入力前に実セルの配置を成立させます。
                    table.Measure(new Size(table.Width, table.Height));
                    table.Arrange(new Rect(0d, 0d, table.Width, table.Height));
                    table.UpdateLayout();
                    table.SelectRowsByPredicate(value => value is LibraryChartRow current && current.path == sourcePath);
                    Assert.IsTrue(table.SelectedIndex >= 0, "現在の保留一覧から入力対象の譜面を選択します。");
                    row = table.ItemsSource[table.SelectedIndex] as LibraryChartRow
                        ?? throw new AssertFailedException("現在の入力対象が譜面行ではありません。");
                    Assert.IsTrue(destinationColumn.IsVisible);
                    Assert.IsTrue(table.IsCurrentCell(table.SelectedIndex, destinationColumn), "配置後の導入先セルを現在セルにします。");
                }

                void AssertOtherUnchanged()
                {
                    // Chartは毎回不変投影を作るため、参照同一性ではなく変更版と利用者に見える値を比較します。
                    ChartFile current = otherEntry.Chart;
                    Assert.AreEqual(otherProjectionVersion, otherEntry.ProjectionVersion);
                    Assert.AreEqual(otherBefore.Status, current.Status);
                    Assert.AreEqual(otherBefore.InstallDestination, current.InstallDestination);
                    Assert.AreEqual(otherBefore.InstallDestinationTitle, current.InstallDestinationTitle);
                    Assert.AreEqual(otherBefore.InstallDestinationArtist, current.InstallDestinationArtist);
                    CollectionAssert.AreEqual(otherBefore.InstallDestinationSuggestions.ToArray(), current.InstallDestinationSuggestions.ToArray());
                    CollectionAssert.AreEqual(otherBefore.Warnings.Select(value => (value.Kind, value.Message)).ToArray(), current.Warnings.Select(value => (value.Kind, value.Message)).ToArray());
                }
            }, prepareViewModel: viewModel =>
            {
                ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(viewModel);
                library = new TestBmsLibrary(songDbPath, getLR2Config: null, _lr2ScoreDB: null, startupRequiredFileScanReason: null,
                    optionsSnapshotProvider: () => options, applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
                    chartFileScanner: scanner, operationAdmission: composition.OperationAdmission, playlistOperationAdmission: composition.PlaylistOperationAdmission, settings: TestBmsFactory.CreateRootSettings([absoluteRoot, destinationRoot]))
                {

                    StartupBackgroundTaskScheduler = (_, _, _, _) => false
                };
                library.Initialize(null, null, BMSLibrary.LibraryInitializeMode.Startup);
                library.ChartPackagesPending = new[] { package, otherPackage };
                BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, db =>
                {
                    db.CreateTable<LR2SongDBExtended.install>();
                    db.InsertOrReplace(package, typeof(LR2SongDBExtended.install));
                    db.InsertOrReplace(otherPackage, typeof(LR2SongDBExtended.install));
                });
                ((IStartupLibraryApplicationPort)viewModel).AttachStartupLibrary(library);
            });
        }
        catch (Exception failure) { primaryFailure = failure; throw; }
        finally
        {
            var cleanupFailures = new List<Exception>();
            try { library?.RequestShutdown("pending-related-presentation-test"); }
            catch (Exception failure) { cleanupFailures.Add(failure); }
            try { drive?.Dispose(); }
            catch (Exception failure) { cleanupFailures.Add(failure); }
            try { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
            catch (Exception failure) { cleanupFailures.Add(failure); }
            if (cleanupFailures.Count != 0)
            {
                if (primaryFailure != null) { cleanupFailures.Insert(0, primaryFailure); }
                throw new AggregateException(cleanupFailures);
            }
        }
    }

    /// <summary>compiledツリーメニューの一覧消去と保留操作が、導入済み・保留の区分、元package、通常・無関係保持の方針を正しい終端へ渡します。</summary>
    [TestMethod]
    public void CompiledTreeMenusPreservePackageSectionsAndPendingOperations()
    {
        var catalogCalls = new List<(string Action, PackageCatalogSection Section, ChartPackage? Package)>();
        var pendingEstimationCalls = new List<(string Action, object Request)>();
        var candidatePolicies = new List<InstallEstimationCandidatePolicy>();
        var pendingInstallationCalls = new List<(string Action, object Request)>();
        var packageCatalogTerminal = new MainWindowPackageCatalogTerminal(
            section =>
            {
                catalogCalls.Add(("clear-all", section, null));
                return Task.FromResult(PackageCatalogMutationResult.Rejected);
            },
            (section, package) =>
            {
                catalogCalls.Add(("remove-package", section, package));
                return Task.FromResult(PackageCatalogMutationResult.Rejected);
            },
            request =>
            {
                catalogCalls.Add(("remove-selection", request.Section, null));
                return Task.FromResult(PackageCatalogMutationResult.Rejected);
            });
        var pendingInstallEstimationTerminal = new MainWindowPendingInstallEstimationTerminal(
            (packages, policy) =>
            {
                pendingEstimationCalls.Add(("search-package", packages.Single()));
                candidatePolicies.Add(policy);
                return Task.CompletedTask;
            },
            request =>
            {
                pendingEstimationCalls.Add(("search-pending", request));
                return Task.CompletedTask;
            },
            request =>
            {
                pendingEstimationCalls.Add(("clear-pending", request));
                return Task.CompletedTask;
            },
            packages =>
            {
                pendingEstimationCalls.Add(("clear-packages", packages.Single()));
                return Task.CompletedTask;
            });
        var pendingInstallationTerminal = new MainWindowPendingInstallationTerminal(
            packages =>
            {
                pendingInstallationCalls.Add(("force-install", packages.Single()));
                return Task.FromResult(PendingPackageMutationResult.Rejected);
            },
            packages =>
            {
                pendingInstallationCalls.Add(("manual-install", packages.Single()));
                return Task.FromResult(PendingPackageMutationResult.Rejected);
            },
            request =>
            {
                pendingInstallationCalls.Add(("install-pending:" + request.Kind, request));
                return Task.FromResult(PendingPackageMutationResult.Rejected);
            });

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (viewModel, window) =>
            {
                var installedMenu = (ContextMenu)window.FindResource("treeViewInstalledContextMenu");
                var pendingMenu = (ContextMenu)window.FindResource("treeViewInstallPendingContextMenu");
                RaiseMenuClick(installedMenu.Items.OfType<MenuItem>().Single());
                RaiseMenuClick(pendingMenu.Items.OfType<MenuItem>().First());

                ChartPackage pendingPackage = new() { path = @"C:\wave6e-pending-package" };
                TreeViewItem pendingTarget = new() { DataContext = pendingPackage };
                var packageMenu = (ContextMenu)window.FindResource("treeViewInstallPackageContextMenu");
                packageMenu.PlacementTarget = pendingTarget;
                MenuItem installMenu = packageMenu.Items.OfType<MenuItem>().Single(item => item.Items.OfType<MenuItem>().Any(child => Equals(child.Header, Resources.Estimate_install_loc)));
                MenuItem[] installCommands = installMenu.Items.OfType<MenuItem>().ToArray();

                foreach (MenuItem command in installCommands)
                {
                    var args = new RoutedEventArgs(MenuItem.ClickEvent, command);
                    Task terminal = MainWindowPresentationTestHarness.CaptureAsyncEventCompletion(() => command.RaiseEvent(args));
                    Assert.IsTrue(args.Handled);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(terminal, "compiled-tree-pending-operation");
                }

                RaiseMenuClick(packageMenu.Items.OfType<MenuItem>().Last());

                ChartPackage installedPackage = new() { path = @"C:\wave6e-installed-package" };
                TreeViewItem installedTarget = new() { DataContext = installedPackage };
                var installedPackageMenu = (ContextMenu)window.FindResource("treeViewInstalledFolderContextMenu");
                installedPackageMenu.PlacementTarget = installedTarget;
                RaiseMenuClick(installedPackageMenu.Items.OfType<MenuItem>().Last());

                Assert.AreEqual(2, catalogCalls.Count(call => call.Action == "clear-all"));
                Assert.IsTrue(catalogCalls.Any(call => call.Action == "clear-all" && call.Section == PackageCatalogSection.Installed));
                Assert.IsTrue(catalogCalls.Any(call => call.Action == "clear-all" && call.Section == PackageCatalogSection.Pending));
                Assert.IsTrue(catalogCalls.Any(call =>
                    call.Action == "remove-package"
                    && call.Section == PackageCatalogSection.Pending
                    && ReferenceEquals(call.Package, pendingPackage)));
                Assert.IsTrue(catalogCalls.Any(call =>
                    call.Action == "remove-package"
                    && call.Section == PackageCatalogSection.Installed
                    && ReferenceEquals(call.Package, installedPackage)));

                CollectionAssert.AreEquivalent(
                    new[]
                    {
                        "search-package",
                        "search-package",
                        "clear-packages"
                    },
                    pendingEstimationCalls.Select(call => call.Action).ToArray());
                CollectionAssert.AreEquivalent(
                    new[] { InstallEstimationCandidatePolicy.RelatedOnly, InstallEstimationCandidatePolicy.KeepUnrelated }, candidatePolicies);
                CollectionAssert.AreEquivalent(
                    new[] { "manual-install", "force-install" },
                    pendingInstallationCalls.Select(call => call.Action).ToArray());
                Assert.IsTrue(pendingEstimationCalls.All(call =>
                    call.Request is ChartPackage package
                        ? ReferenceEquals(package, pendingPackage)
                        : call.Request is PendingInstallPackageOperationRequest operation
                            ? operation.Targets.Count == 0
                            : true));
                Assert.IsTrue(pendingInstallationCalls.All(call =>
                    call.Request is ChartPackage package
                        ? ReferenceEquals(package, pendingPackage)
                        : call.Request is PendingInstallPackageOperationRequest operation
                            ? operation.Targets.Count == 0
                            : true));
            },
            packageCatalogTerminal: packageCatalogTerminal,
            pendingInstallEstimationTerminal: pendingInstallEstimationTerminal,
            pendingInstallationTerminal: pendingInstallationTerminal);
    }

    /// <summary>packageと選択譜面の手動・強制導入クリックが同じreceipt通知へ接続し、後片付け失敗と確定済み変更を報告します。</summary>
    [DataTestMethod]
    [DataRow(false, false)] // packageの強制導入。
    [DataRow(false, true)] // packageの手動導入。
    [DataRow(true, false)] // 選択譜面の強制導入。
    [DataRow(true, true)] // 選択譜面の手動導入。
    public void CompiledPendingInstallRoutesReportReceipt(bool chartRoute, bool manual)
    {
        var failure = new System.IO.IOException("four-route-cleanup-marker");
        var sessionReceipt = new LibraryMutationSessionReceipt(
            [new LibraryMutationSessionTarget(@"C:\pending-source", @"D:\installed")],
            durableCommit: true,
            cleanupFailure: failure);
        var result = PendingPackageMutationResult.FromTerminal(sessionReceipt);
        var dialogs = new FileDbReportRecordingDialogs();
        var pendingMutationViewTerminal = new MainWindowPendingPackageMutationViewTerminal(
            () => false, () => 1, _ => Task.FromResult(true), dialogs);
        int calls = 0;
        Task<PendingPackageMutationResult> Complete() { calls++; return Task.FromResult(result); }
        var pendingInstallationTerminal = new MainWindowPendingInstallationTerminal(
            _ => Complete(), _ => Complete(), _ => Complete());
        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (viewModel, window) =>
            {
                ContextMenu menu;
                if (chartRoute)
                {
                    ChartFile chart = (ChartTestValues.Empty() with { Path = @"C:\pending-source\chart.bms", Md5 = new string('a', 32) });
                    var entry = PackageChartEntry.FromChart((chart));
                    var row = LibraryChartRow.FromPackageChartEntry(entry);
                    var table = (CustomTableView)window.FindName("customTableView");
                    table.ItemsSource = new List<object> { row };
                    table.SelectRowsByPredicate(_ => true);
                    viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.PendingInstallFolderSelected);
                    menu = (ContextMenu)window.FindResource("tableContextMenu");
                    menu.PlacementTarget = new FrameworkElement { DataContext = row };
                }
                else
                {
                    menu = (ContextMenu)window.FindResource("treeViewInstallPackageContextMenu");
                    menu.PlacementTarget = new TreeViewItem { DataContext = new ChartPackage { path = @"C:\pending-source" } };
                }
                MenuItem group = menu.Items.OfType<MenuItem>()
                    .Single(item => item.Items.OfType<MenuItem>().Any(child => Equals(child.Header, Resources.Estimate_install_loc)));
                RaiseMenuClick(group.Items.OfType<MenuItem>().Single(item => Equals(item.Header, manual ? Resources.Install_to_estimation : Resources.Force_install)));
                Assert.AreEqual(1, calls);
                Assert.AreEqual(1, dialogs.Messages.Count);
                Assert.AreEqual(System.Windows.MessageBoxImage.Warning, dialogs.Messages[0].Icon);
                StringAssert.Contains(dialogs.Messages[0].MessageBoxText, failure.Message);
            },
            pendingInstallationTerminal: pendingInstallationTerminal,
            pendingPackageMutationViewTerminal: pendingMutationViewTerminal);
    }

    /// <summary>compiledフォルダ改名と全再検査メニューが、選択パスを改名終端へ渡し、再検査終端を一度だけ呼びます。</summary>
    [TestMethod]
    public void CompiledMaintenanceAndAutoRenameMenusUseTypedTerminals()
    {
        var autoRenamePaths = new List<string>();
        var maintenanceCalls = new List<int>();
        var autoRenameTerminal = new MainWindowFolderAutoRenameTerminal(
            targets =>
            {
                Assert.IsNotNull(targets);
                return true;
            },
            path =>
            {
                autoRenamePaths.Add(path);
                return Task.CompletedTask;
            });
        var maintenanceTerminal = new MainWindowMaintenanceRescanTerminal(() =>
        {
            maintenanceCalls.Add(1);
            return Task.FromResult(MaintenanceRescanStartResult.Rejected);
        });

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (viewModel, window) =>
            {
                var libraryMenu = (ContextMenu)window.FindResource("treeViewLibraryFolderContextMenu");
                TreeViewItem libraryTarget = new() { Header = @"C:\wave6e-library" };
                libraryMenu.PlacementTarget = libraryTarget;
                MenuItem renameAll = libraryMenu.Items.OfType<MenuItem>().Last();
                RaiseMenuClick(renameAll);

                var tableMenu = (ContextMenu)window.FindResource("tableContextMenu");
                MenuItem fullScanMenu = tableMenu.Items
                    .OfType<MenuItem>()
                    .First(item => item.Name == "tableContextMenuItemFullScanCheck");
                MenuItem fullRescan = fullScanMenu.Items
                    .OfType<MenuItem>()
                    .First(item => item.Name == "tableContextMenuItemFullScanCheckAllCharts");
                RaiseMenuClick(fullRescan);

                Assert.AreEqual(1, autoRenamePaths.Count);
                Assert.AreEqual(@"C:\wave6e-library", autoRenamePaths[0]);
                Assert.AreEqual(1, maintenanceCalls.Count);
            },
            folderAutoRenameTerminal: autoRenameTerminal,
            maintenanceRescanTerminal: maintenanceTerminal);
    }

    /// <summary>混在選択のcompiled自動改名クリックがBMS・bmson両方の元tokenを終端へ渡し、一方を欠落させません。</summary>
    [TestMethod]
    public void CompiledAutoRenameSelectionPreservesBmsAndBmsonTargets()
    {
        IReadOnlyList<ChartOperationTarget>? capturedTargets = null;
        var autoRenameTerminal = new MainWindowFolderAutoRenameTerminal(
            targets =>
            {
                capturedTargets = targets;
                return true;
            },
            _ => Task.CompletedTask);

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (viewModel, window) =>
            {
                ChartFile bmsFile = ChartTestValues.Empty() with
                {
                    Path = @"C:\wave6e-library\bms\chart.bms",
                    Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    RawTitle = "BMS chart",
                    RawArtist = "Artist"
                };
                ChartFile bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
                {
                    Path = @"C:\wave6e-library\bmson\chart.bmson",
                    Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    RawTitle = "bmson chart",
                    RawArtist = "Artist"
                };
                var bmsRow = LibraryChartRow.FromChartFile(bmsFile);
                var bmsonRow = LibraryChartRow.FromChartFile((bmsonSong));
                var rows = new List<object> { bmsRow, bmsonRow };

                var table = (CustomTableView)window.FindName("customTableView");
                Assert.IsNotNull(table);
                table.ItemsSource = rows;
                table.SelectRowsByPredicate(_ => true);
                viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.FolderFilterSelected);

                var tableMenu = (ContextMenu)window.FindResource("tableContextMenu");
                tableMenu.PlacementTarget = new FrameworkElement { DataContext = bmsRow };
                tableMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, tableMenu));
                MenuItem rename = tableMenu.Items
                    .OfType<MenuItem>()
                    .First(item => item.Name == "tableContextMenuItemAutoRenameFolder");
                RaiseMenuClick(rename);

                Assert.IsNotNull(capturedTargets);
                Assert.AreEqual(2, capturedTargets.Count);
                Assert.IsTrue(capturedTargets.Any(target =>
                    target.Chart.Kind == ChartFileKind.Bms
                    && ReferenceEquals(target.Chart.Token, bmsFile.Token)));
                Assert.IsTrue(capturedTargets.Any(target =>
                    target.Chart.Kind == ChartFileKind.Bmson
                    && ReferenceEquals(target.Chart.Token, bmsonSong.Token)));
            },
            folderAutoRenameTerminal: autoRenameTerminal);
    }

    /// <summary>保留の一括保守三操作が各終端だけを一度呼び、未完了の非同期処理中からクリックを処理済みにします。</summary>
    [TestMethod]
    public void CompiledPendingBulkMenuUsesOneTypedTerminalPerOperation()
    {
        var calls = new List<string>();
        var deleteSources = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var renameZeroNotes = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overwriteResources = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bulkTerminal = new MainWindowPendingBulkMaintenanceTerminal(
            () =>
            {
                calls.Add("delete-sources");
                return deleteSources.Task;
            },
            () =>
            {
                calls.Add("rename-zero-notes");
                return renameZeroNotes.Task;
            },
            () =>
            {
                calls.Add("overwrite-resources");
                return overwriteResources.Task;
            });

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (_, window) =>
            {
                try
                {

                    var pendingMenu = (ContextMenu)window.FindResource("treeViewInstallPendingContextMenu");
                    MenuItem advanced = pendingMenu.Items
                        .OfType<MenuItem>()
                        .Single(item => item.Items.OfType<MenuItem>().Count() == 3);

                    // 表示順ではなく操作の表示資源でクリック対象を選び、各終端の接続を確認します。
                    string[] headers = [Resources.Delete_pending_installed_only_packages_permanently, Resources.Overwrite_pending_installed_only_packages_resources, Resources.Rename_pending_zero_note_to_invalid_ext];
                    TaskCompletionSource<object?>[] completions =
                    [deleteSources, overwriteResources, renameZeroNotes];
                    string[] expected = ["delete-sources", "overwrite-resources", "rename-zero-notes"];
                    MenuItem[] commands = headers.Select(header => advanced.Items.OfType<MenuItem>().Single(item => Equals(item.Header, header))).ToArray();
                    Assert.AreEqual(3, commands.Length);
                    for (int i = 0; i < commands.Length; i++)
                    {
                        var args = new RoutedEventArgs(MenuItem.ClickEvent, commands[i]);
                        commands[i].RaiseEvent(args);
                        Assert.IsTrue(args.Handled);
                        Assert.IsFalse(completions[i].Task.IsCompleted);
                        completions[i].SetResult(null);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(completions[i].Task, "compiled-terminal-delegate-result");
                        Assert.AreEqual(expected[i], calls[i]);
                    }
                    Assert.AreEqual(3, calls.Count);
                }
                finally
                {
                    deleteSources.TrySetResult(null);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(deleteSources.Task, "compiled-finally-delegate");
                    renameZeroNotes.TrySetResult(null);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(renameZeroNotes.Task, "compiled-finally-delegate");
                    overwriteResources.TrySetResult(null);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(overwriteResources.Task, "compiled-finally-delegate");
                }

            },
            pendingBulkMaintenanceTerminal: bulkTerminal);
    }

    /// <summary>初回解析中の後発プレイリスト選択とBindingを保ち、現在の重複選択では解析前の文脈更新と準備済みツリー展開を確認します。</summary>
    [DataTestMethod]
    [DataRow("later")]
    [DataRow("current")]
    [DataRow("busy")]
    public void ColdDuplicateTree_LaterPlaylistRootKeepsSelectionAndVisibleSummary(string condition)
    {
        bool currentRoot = condition == "current";
        var busyDialogs = new FileDbReportRecordingDialogs();
        RegularChartListOwnerTestSupport.WithTemporarySongDb(songDb =>
        {
            Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.StartupSelectInstallPending = false;
            });
            var presentationTasks = new RegularChartNavigationTests.ProjectionTaskCollector(new WpfUiScheduler(() => TestUiDispatcherHost.Dispatcher));
            DuplicateNavigationTestFixture? fixture = null;
            Task? firstHandler = null;
            var maintenance = new MainWindowMaintenanceTreeTerminal((mode, parameter) =>
                (fixture ?? throw new AssertFailedException("cold fixture未接続です。")).NavigateDuplicate(mode, parameter));
            try
            {
                MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(settings,
                    (scope, vm, window) =>
                    {
                        DuplicateNavigationTestFixture current = fixture ?? throw new AssertFailedException();
                        MainWindowPresentationTestHarness.ShowCompiledContent(scope, vm, window);
                        var duplicateRoot = (TreeViewItem)window.FindName("treeViewItemSearchDuplicated");
                        var playlistRoot = (TreeViewItem)window.FindName("treeViewItemPlaylist");
                        var tree = (TreeView)window.FindName("treeViewPlaylist");
                        var summary = (CustomTableView)window.FindName("customTablePlaylistSummary");
                        if (condition == "busy")
                        {
                            playlistRoot.IsSelected = true;
                            current.AwaitSummary();
                            vm.ChartFilters.KeywordFilter = "target";
                            var sourceProperty = DependencyPropertyDescriptor.FromProperty(CustomTableView.ItemsSourceProperty, typeof(CustomTableView));
                            var bound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            EventHandler changed = (_, _) =>
                            {
                                if (summary.ItemsSource?.Cast<PlaylistSummaryRow>().Any(row => row.Name == "target playlist") == true)
                                { bound.TrySetResult(); }
                            };
                            sourceProperty.AddValueChanged(summary, changed);
                            try
                            {
                                changed(summary, EventArgs.Empty);
                                TestUiDispatcherHost.AwaitPresentationOnDispatcher(bound.Task, "Busy initial summary Binding");
                                RegularChartNavigationRequest selection = vm.RegularChartList.CaptureNavigation();
                                IList visibleRows = summary.ItemsSource;
                                Assert.IsTrue(current.Library.OperationAdmission.TryEnter(out IDisposable other));
                                try
                                {
                                    firstHandler = MainWindowPresentationTestHarness.CaptureAsyncEventCompletion(() => duplicateRoot.IsSelected = true);
                                    TestUiDispatcherHost.AwaitTaskOnDispatcher(firstHandler, "cold tree Busy actual handler");
                                    Assert.AreSame(selection, vm.RegularChartList.CaptureNavigation());
                                    Assert.AreEqual("target", vm.ChartFilters.KeywordFilter);
                                    Assert.IsTrue(vm.PlaylistWorkspace.IsPlaylistSummaryModeRequested);
                                    Assert.AreEqual(Visibility.Visible, summary.Visibility);
                                    Assert.AreSame(visibleRows, summary.ItemsSource);
                                    Assert.AreEqual(Resources.Warn_LibraryOperationBusy, busyDialogs.Messages.Single().MessageBoxText);
                                    Assert.IsNull(current.Library.DuplicateChartGroups);
                                }
                                finally { other.Dispose(); }
                                TestUiDispatcherHost.ProcessQueuedPresentation();
                                Assert.IsNull(current.Library.DuplicateChartGroups);
                            }
                            finally { sourceProperty.RemoveValueChanged(summary, changed); }
                            return;
                        }
                        if (currentRoot)
                        {
                            current.Table.name = "duplicate target playlist";
                            playlistRoot.IsSelected = true;
                            vm.PlaylistWorkspace.RebuildPlaylistSummaryView(runAsync: false);
                            vm.ChartFilters.KeywordFilter = "duplicate";
                            Assert.IsTrue(vm.PlaylistWorkspace.IsPlaylistSummaryModeRequested);
                            duplicateRoot.IsExpanded = false;
                            var actualTable = (CustomTableView)window.FindName("customTableView");
                            var binding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            var sourceProperty = DependencyPropertyDescriptor.FromProperty(CustomTableView.ItemsSourceProperty, typeof(CustomTableView));
                            EventHandler bound = (_, _) =>
                            {
                                if (actualTable.ItemsSource?.Cast<LibraryChartRow>().Select(row => row.path).Order(StringComparer.Ordinal)
                                    .SequenceEqual(current.Duplicates.Select(chart => chart.Path).Order(StringComparer.Ordinal)) == true)
                                { binding.TrySetResult(); }
                            };
                            sourceProperty.AddValueChanged(actualTable, bound);
                            current.EnableGate();
                            firstHandler = MainWindowPresentationTestHarness.CaptureAsyncEventCompletion(() => duplicateRoot.IsSelected = true);
                            try
                            {
                                current.AwaitGate(); current.AwaitGroupsWhileGated();
                                Assert.IsFalse(vm.PlaylistWorkspace.IsPlaylistSummaryModeRequested, "現在の重複選択は解析前にサマリーを解除します。");
                                Assert.AreEqual(GridKeywordSearchContext.ChartList, vm.ChartFilters.CurrentKeywordSearchContext);
                                Assert.IsTrue(duplicateRoot.IsSelected);
                                current.CompleteDuplicate();
                                TestUiDispatcherHost.AwaitTaskOnDispatcher(firstHandler, "current root actual Selected terminal");
                                TestUiDispatcherHost.ProcessQueuedPresentation();
                                Assert.IsTrue(duplicateRoot.IsExpanded);
                                Assert.AreEqual(MainViewUpdateMode.DuplicateFilterSelected, vm.MainChartList.LastAppliedColumnMode);
                                CollectionAssert.AreEquivalent(current.Duplicates.Select(chart => chart.Path).ToArray(), vm.MainChartList.Rows.Cast<LibraryChartRow>().Select(row => row.path).ToArray());
                                TestUiDispatcherHost.AwaitPresentationOnDispatcher(binding.Task, "current duplicate actual ItemsSource Binding");
                                Assert.AreSame(vm.MainChartList.Rows, actualTable.ItemsSource);
                                CollectionAssert.AreEquivalent(current.Duplicates.Select(chart => chart.Path).ToArray(), actualTable.ItemsSource.Cast<LibraryChartRow>().Select(row => row.path).ToArray());
                            }
                            finally
                            {
                                current.ReleaseProducer();
                                try { TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(firstHandler, current.AwaitReadyAsync(awaitRows: false)), "current root actual handler and display cleanup"); }
                                finally { sourceProperty.RemoveValueChanged(actualTable, bound); }
                            }
                            return;
                        }
                        current.EnableGate();
                        firstHandler = MainWindowPresentationTestHarness.CaptureAsyncEventCompletion(() => duplicateRoot.IsSelected = true);
                        bool staleFailed = false;
                        DependencyPropertyDescriptor? itemsSource = null;
                        EventHandler? sourceChanged = null;
                        PropertyChangedEventHandler? modeChanged = null;
                        RoutedPropertyChangedEventHandler<object>? selectionChanged = null;
                        try
                        {
                            current.AwaitGate();
                            // playlistRootSelectは同期イベントなので、イベント用の非同期完了通知では待たない。
                            // プレイリストを選択した後、そのサマリー構築と実Bindingを観測する。
                            playlistRoot.IsSelected = true;
                            current.AwaitSummary();
                            var bound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            itemsSource = DependencyPropertyDescriptor.FromProperty(CustomTableView.ItemsSourceProperty, typeof(CustomTableView));
                            sourceChanged = (_, _) =>
                            {
                                if (summary.ItemsSource?.Cast<PlaylistSummaryRow>().Any(row => row.Name == "target playlist") == true)
                                { bound.TrySetResult(); }
                            };
                            itemsSource.AddValueChanged(summary, sourceChanged);
                            sourceChanged(summary, EventArgs.Empty);
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(bound.Task, "actual summary rows Binding");
                            var violations = new List<string>();
                            modeChanged = (_, args) =>
                            {
                                if (args.PropertyName is nameof(PlaylistWorkspaceViewModel.IsPlaylistSummaryMode)
                                    or nameof(PlaylistWorkspaceViewModel.IsPlaylistSummaryModeRequested))
                                {
                                    if (!vm.PlaylistWorkspace.IsPlaylistSummaryModeRequested || !vm.PlaylistWorkspace.IsPlaylistSummaryMode)
                                    { violations.Add("古い重複要求がsummaryを解除しました。"); }
                                }
                            };
                            selectionChanged = (_, _) =>
                            {
                                if (!playlistRoot.IsSelected) { violations.Add("古い重複要求が左選択を変更しました。"); }
                            };
                            vm.PlaylistWorkspace.PropertyChanged += modeChanged;
                            tree.SelectedItemChanged += selectionChanged;
                            Assert.IsTrue(playlistRoot.IsSelected);
                            Assert.IsFalse(duplicateRoot.IsSelected);
                            current.CompleteDuplicate();
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(firstHandler, "cold duplicate actual Selected handler");
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            Assert.AreEqual(0, violations.Count, string.Join(Environment.NewLine, violations));
                            Assert.IsTrue(playlistRoot.IsSelected);
                            Assert.IsFalse(duplicateRoot.IsSelected);
                            Assert.AreSame(playlistRoot, tree.SelectedItem);
                            Assert.AreEqual(Visibility.Visible, summary.Visibility);
                            CollectionAssert.AreEqual(new[] { "target playlist" },
                                summary.ItemsSource.Cast<PlaylistSummaryRow>().Select(row => row.Name).ToArray());
                        }
                        catch { staleFailed = true; throw; }
                        finally
                        {
                            current.ReleaseProducer();
                            try
                            {
                                Task stop = staleFailed ? vm.RegularChartList.StopAsync() : Task.CompletedTask;
                                TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(stop,
                                    firstHandler ?? Task.CompletedTask, current.AwaitReadyAsync(awaitRows: false)), "cold Selected/data/Rows cleanup");
                            }
                            catch (Exception cleanup) when (staleFailed) { Console.WriteLine("cold stale cleanup fault: " + cleanup.GetType().Name); }
                            finally
                            {
                                try { current.Dispose(); }
                                catch (Exception cleanup) when (staleFailed) { Console.WriteLine("cold stale fixture cleanup fault: " + cleanup.GetType().Name); }
                                finally
                                {
                                    if (modeChanged != null) { vm.PlaylistWorkspace.PropertyChanged -= modeChanged; }
                                    if (selectionChanged != null) { tree.SelectedItemChanged -= selectionChanged; }
                                    if (itemsSource != null && sourceChanged != null) { itemsSource.RemoveValueChanged(summary, sourceChanged); }
                                }
                            }
                        }
                    }, maintenanceTreeTerminal: maintenance,
                    prepareViewModel: vm =>
                    {
                        fixture = new DuplicateNavigationTestFixture(vm, songDb, settings, drainPresentations: () => presentationTasks.DrainPriorAsync());
                        vm.RegularChartList.ConfigureDuplicateNavigationDialogs(busyDialogs);
                    }, uiScheduler: presentationTasks);
            }
            finally { if (fixture != null) { TestUiDispatcherHost.Invoke(fixture.Dispose); } }
        });
    }

    /// <summary>同名の実検索結果を実Treeで手動選択し、本番composition/storeの二folder統合後に操作前の次群だけを表示・選択します。旧行選択を別群へ持ち越しません。</summary>
    [TestMethod]
    public void DuplicateSelections_ActualMergeSelectsCapturedNextGroupThroughCompiledTree()
    {
        RunDuplicateSelectionFixture((scope, viewModel, window, library, charts, navigation) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            var root = (TreeViewItem)window.FindName("treeViewItemSearchDuplicated");
            DuplicateGroup first = library.DuplicateChartGroups[0];
            DuplicateGroup next = library.DuplicateChartGroups[1];
            Assert.AreEqual(first.Header, next.Header);
            // 同見出し時の並び自体は固定せず、現在先頭/次に対応する独立fixtureの二組を選びます。
            ChartFile[] firstCharts = first.Folders.Contains(charts[0].Directory) ? charts[..2] : charts[2..];
            ChartFile[] nextCharts = first.Folders.Contains(charts[0].Directory) ? charts[2..] : charts[..2];
            SelectDuplicateTreeAndAwaitRows(viewModel, window, navigation, () => root.IsSelected = true, charts.Select(chart => chart.Path).ToArray());
            TreeViewItem nextItem = RealizeDuplicateGroupItem(root, next);
            SelectDuplicateTreeAndAwaitRows(viewModel, window, navigation, () => nextItem.IsSelected = true, nextCharts[0].Path, nextCharts[1].Path);
            TreeViewItem nextFolder = RealizeDuplicateFolderItem(root, next, nextCharts[0].Directory);
            SelectDuplicateTreeAndAwaitRows(viewModel, window, navigation, () => nextFolder.IsSelected = true, nextCharts[0].Path);
            TreeViewItem sourceFolder = RealizeDuplicateFolderItem(root, first, firstCharts[0].Directory);
            SelectDuplicateTreeAndAwaitRows(viewModel, window, navigation, () => sourceFolder.IsSelected = true, firstCharts[0].Path);
            var table = (CustomTableView)window.FindName("customTableView");
            table.SelectedIndex = 0;
            Assert.AreEqual(1, table.GetSelectedRowsSnapshot().Count);
            MenuItem merge = CreateCompiledDuplicateMerge(window, sourceFolder, firstCharts[1].Directory);
            using var release = new ManualResetEventSlim(false);
            var requiredNotification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int claimed = 0;
            PropertyChangedEventHandler committed = (_, args) =>
            {
                if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion)
                    && library.DuplicateChartGroups?.Count == 1 && !Directory.Exists(firstCharts[0].Directory)
                    && Interlocked.Exchange(ref claimed, 1) == 0)
                { requiredNotification.TrySetResult(); release.Wait(); }
            };
            library.PropertyChanged += committed;
            try
            {
                SelectDuplicateTreeAndAwaitRows(viewModel, window, navigation, () => RaiseMenuClick(merge),
                    new[] { nextCharts[0].Path, nextCharts[1].Path }, handler =>
                    {
                        try
                        {
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(requiredNotification.Task, handler, "actual merge required notification"), "actual merge notification gate");
                            Assert.IsFalse(handler.IsCompleted);
                            Assert.IsTrue(library.OperationAdmission.IsActive);
                            Assert.IsFalse(library.OperationAdmission.TryEnter(out IDisposable rejected));
                            Assert.IsNull(rejected);
                            Assert.IsFalse(library.IsWriteLockHeldInitializeMin || library.IsWriteLockHeldInitializeBMSFiles || library.IsWriteLockHeldDuplicateChartGroups);
                        }
                        finally { release.Set(); }
                    });
            }
            finally { release.Set(); library.PropertyChanged -= committed; }
            var left = (TreeView)window.FindName("treeView");
            Assert.IsInstanceOfType(left.SelectedItem, typeof(DuplicateGroup));
            var selected = (DuplicateGroup)left.SelectedItem;
            CollectionAssert.AreEquivalent(new[] { nextCharts[0].Directory, nextCharts[1].Directory }, selected.Folders);
            Assert.IsFalse(Directory.Exists(firstCharts[0].Directory));
            Assert.IsTrue(File.Exists(firstCharts[1].Path));
            IReadOnlyList<object> selectedRows = table.GetSelectedRowsSnapshot();
            Assert.AreEqual(0, selectedRows.Count, "旧群の選択を持ち越しません。selected paths="
                + string.Join("|", selectedRows.OfType<LibraryChartRow>().Select(row => row.Chart.Path))
                + " table index=" + table.SelectedIndex);
            Assert.IsFalse(library.OperationAdmission.IsActive);
        });
    }

    /// <summary>実統合の必要通知中に、自動選択候補とは異なる第三群へ移動します。成功後も後発群の実Tree・一覧・検索条件を保ち、元の次群へ一時的にも切り替えません。</summary>
    [TestMethod]
    public void DuplicateSelections_ActualMergeKeepsLaterGroupInsteadOfCapturedNextGroup()
    {
        RunDuplicateSelectionFixture((scope, viewModel, window, library, charts, navigation) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            var root = (TreeViewItem)window.FindName("treeViewItemSearchDuplicated");
            DuplicateGroup source = library.DuplicateChartGroups[0];
            DuplicateGroup automatic = library.DuplicateChartGroups[1];
            DuplicateGroup later = library.DuplicateChartGroups[2];
            ChartFile[] sourceCharts = charts.Where(chart => source.Folders.Contains(chart.Directory)).ToArray();
            string[] laterPaths = charts.Where(chart => later.Folders.Contains(chart.Directory)).Select(chart => chart.Path).ToArray();
            string[] laterFolders = later.Folders.ToArray();
            Assert.IsNull(DuplicateViewContext.CaptureGroup(automatic).ResolveGroup([later]));
            viewModel.ChartFilters.KeywordFilter = "same";
            TreeViewItem sourceFolder = RealizeDuplicateFolderItem(root, source, sourceCharts[0].Directory);
            SelectDuplicateTreeAndAwaitRows(viewModel, window, navigation, () => sourceFolder.IsSelected = true, sourceCharts[0].Path);
            MenuItem merge = CreateCompiledDuplicateMerge(window, sourceFolder, sourceCharts[1].Directory);
            using var release = new ManualResetEventSlim(false);
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int claimed = 0;
            PropertyChangedEventHandler committed = (_, args) =>
            {
                if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion)
                    && library.DuplicateChartGroups?.Count == 2 && !Directory.Exists(sourceCharts[0].Directory)
                    && Interlocked.Exchange(ref claimed, 1) == 0)
                { reached.TrySetResult(); release.Wait(); }
            };
            var violations = new List<string>();
            RegularChartNavigationRequest? laterNavigation = null;
            Task? laterHandler = null;
            PropertyChangedEventHandler rowsChanged = (_, args) =>
            {
                if (args.PropertyName == nameof(MainChartListViewModel.Rows) && laterNavigation != null
                    && !viewModel.MainChartList.Rows.Cast<LibraryChartRow>().Select(row => row.Chart.Path).OrderBy(path => path, StringComparer.Ordinal)
                        .SequenceEqual(laterPaths.OrderBy(path => path, StringComparer.Ordinal)))
                { violations.Add("後発群以外の一覧が公開されました。"); }
            };
            RoutedPropertyChangedEventHandler<object> selectionChanged = (_, _) =>
            {
                if (laterNavigation != null && ((TreeView)window.FindName("treeView")).SelectedItem is DuplicateGroup selected
                    && !selected.Folders.OrderBy(path => path, StringComparer.Ordinal).SequenceEqual(laterFolders.OrderBy(path => path, StringComparer.Ordinal)))
                { violations.Add("元の統合が別群を選択しました。"); }
            };
            PropertyChangedEventHandler searchChanged = (_, _) =>
            {
                if (laterNavigation != null && (viewModel.ChartFilters.KeywordFilter != "same"
                    || viewModel.ChartFilters.CurrentKeywordSearchContext != GridKeywordSearchContext.ChartList))
                { violations.Add("元の統合が後発群の検索条件を変更しました。"); }
            };
            library.PropertyChanged += committed;
            viewModel.ChartFilters.PropertyChanged += searchChanged;
            viewModel.MainChartList.PropertyChanged += rowsChanged;
            var tree = (TreeView)window.FindName("treeView");
            tree.SelectedItemChanged += selectionChanged;
            try
            {
                SelectDuplicateTreeAndAwaitRows(viewModel, window, navigation, () => RaiseMenuClick(merge), laterPaths, handler =>
                {
                    try
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(reached.Task, handler,
                            "merge ready before later group"), "merge actual notification");
                        Assert.IsTrue(library.OperationAdmission.IsActive);
                        Assert.IsFalse(handler.IsCompleted);
                        // モデルの最新参照とBindingの配送は別なので、実Treeに現在表示された同じ所属の項目を選ぶ。
                        DuplicateGroup currentLater = root.Items.Cast<DuplicateGroup>().Single(group => group.Folders.Contains(laterFolders[0]));
                        TreeViewItem laterItem = RealizeDuplicateGroupItem(root, currentLater);
                        // 同じ解析の必要通知はまだ保留中なので、選択は受理だけを確認し、開始したハンドラーは解放後に回収する。
                        laterHandler = MainWindowPresentationTestHarness.CaptureAsyncEventCompletion(() => laterItem.IsSelected = true);
                        laterNavigation = viewModel.RegularChartList.CaptureNavigation();
                        Assert.AreSame(currentLater, tree.SelectedItem);
                    }
                    finally { release.Set(); }
                });
                if (laterHandler != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(laterHandler, "later group Selected handler"); }
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(0, violations.Count, string.Join(Environment.NewLine, violations));
                Assert.IsTrue(viewModel.RegularChartList.IsCurrentNavigation(laterNavigation));
                Assert.IsInstanceOfType(tree.SelectedItem, typeof(DuplicateGroup));
                CollectionAssert.AreEquivalent(laterFolders, ((DuplicateGroup)tree.SelectedItem).Folders);
                Assert.AreEqual("same", viewModel.ChartFilters.KeywordFilter);
                Assert.IsFalse(Directory.Exists(sourceCharts[0].Directory));
                Assert.IsTrue(File.Exists(sourceCharts[1].Path));
                Assert.IsFalse(library.OperationAdmission.IsActive);
            }
            finally
            {
                release.Set();
                try
                { if (laterHandler != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(laterHandler, "later group handler cleanup"); } }
                finally
                {
                    library.PropertyChanged -= committed;
                    viewModel.ChartFilters.PropertyChanged -= searchChanged;
                    viewModel.MainChartList.PropertyChanged -= rowsChanged;
                    tree.SelectedItemChanged -= selectionChanged;
                }
            }
        }, groupCount: 3);
    }

    /// <summary>実Gキーで単一フォルダ内の同内容ファイルを整理します。通常成功は次群を選び、削除前に別画面へ移動した条件では実失効後もそのTree・一覧・検索を保ち、古い空一覧を反映しません。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DuplicateSelections_ActualHashCleanupRespectsLaterViewAndKeepsNormalAutoSelection(bool laterView)
    {
        MainWindowViewModel? actualViewModel = null;
        var terminal = new MainWindowDuplicateMaintenanceTerminal(
            (source, destination, group) => (actualViewModel ?? throw new AssertFailedException()).DuplicateMaintenanceWorkflow.RunFolderMergeAsync(source, destination, group),
            (group, folder) => (actualViewModel ?? throw new AssertFailedException()).DuplicateMaintenanceWorkflow.RunHashCleanupAsync(group, folder),
            () => true);
        using var release = new ManualResetEventSlim(false);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutations = new OwnedChartCollectionTestSupport.TestFileMutationService
        {
            BeforeFileDelete = _ => { reached.TrySetResult(); release.Wait(); }
        };
        RunDuplicateSelectionFixture((scope, viewModel, window, library, charts, navigation) =>
        {
            actualViewModel = viewModel;
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            var root = (TreeViewItem)window.FindName("treeViewItemSearchDuplicated");
            DuplicateGroup source = library.DuplicateChartGroups[0];
            Assert.AreEqual(1, source.Folders.Count);
            DuplicateGroup next = library.DuplicateChartGroups[1];
            string[] expectedPaths = next.ChartFiles.Select(chart => chart.Path).ToArray();
            ChartFile[] original = charts.Where(chart => chart.Directory == source.Folders[0]).ToArray();
            Assert.AreEqual(2, original.Length);
            TreeViewItem folder = RealizeDuplicateFolderItem(root, source, source.Folders[0]);
            SelectDuplicateTreeAndAwaitRows(viewModel, window, navigation, () => folder.IsSelected = true, original.Select(chart => chart.Path).ToArray());
            var tree = (TreeView)window.FindName("treeView");
            var normal = (TreeViewItem)window.FindName("treeViewItemFullScanCheckAllCharts");
            var violations = new List<string>();
            RegularChartNavigationRequest? laterNavigation = null;
            PropertyChangedEventHandler rowsChanged = (_, args) =>
            {
                if (laterNavigation != null && args.PropertyName == nameof(MainChartListViewModel.Rows)
                    && (viewModel.MainChartList.LastAppliedColumnMode != MainViewUpdateMode.FullScanAllChartsFilterSelected
                        || !viewModel.MainChartList.Rows.Cast<LibraryChartRow>().Select(row => row.Chart.Path).OrderBy(path => path, StringComparer.Ordinal)
                            .SequenceEqual(expectedPaths.OrderBy(path => path, StringComparer.Ordinal))))
                { violations.Add("旧Hash結果が後発画面へ一覧を反映しました。"); }
            };
            RoutedPropertyChangedEventHandler<object> selectionChanged = (_, _) =>
            {
                if (laterNavigation != null && !normal.IsSelected) { violations.Add("旧Hash結果が後発Tree選択を変更しました。"); }
            };
            PropertyChangedEventHandler searchChanged = (_, _) =>
            {
                if (laterNavigation != null && (viewModel.ChartFilters.KeywordFilter != "same"
                    || viewModel.ChartFilters.CurrentKeywordSearchContext != GridKeywordSearchContext.ChartList))
                { violations.Add("旧Hash結果が後発画面の検索条件を変更しました。"); }
            };
            viewModel.ChartFilters.PropertyChanged += searchChanged;
            viewModel.MainChartList.PropertyChanged += rowsChanged;
            tree.SelectedItemChanged += selectionChanged;
            try
            {
                SelectDuplicateTreeAndAwaitRows(viewModel, window, navigation, () =>
                {
                    var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(folder), 0, Key.G)
                    { RoutedEvent = UIElement.KeyDownEvent, Source = folder };
                    folder.RaiseEvent(key);
                    Assert.IsTrue(key.Handled);
                }, expectedPaths, handler =>
                {
                    try
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(reached.Task, handler,
                            "real hash file deletion"), "hash physical gate");
                        Assert.IsTrue(library.OperationAdmission.IsActive);
                        Assert.IsFalse(handler.IsCompleted);
                        if (laterView)
                        {
                            viewModel.ChartFilters.KeywordFilter = "same";
                            SelectDuplicateTreeAndAwaitRows(viewModel, window, navigation, () => normal.IsSelected = true, expectedPaths);
                            laterNavigation = viewModel.RegularChartList.CaptureNavigation();
                        }
                    }
                    finally { release.Set(); }
                });
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(0, violations.Count, string.Join(Environment.NewLine, violations));
                Assert.AreEqual(1, mutations.FileDeleteCalls);
                string[] surviving = original.Where(chart => File.Exists(chart.Path)).Select(chart => chart.Path).ToArray();
                Assert.AreEqual(1, surviving.Length);
                string removed = original.Single(chart => !File.Exists(chart.Path)).Path;
                Assert.IsFalse(library.BmsCharts.Any(chart => chart.Path == removed));
                using (var db = new LR2SongDBExtended(Path.Combine(Path.GetDirectoryName(original[0].Directory) ?? throw new AssertFailedException(), "song.db")))
                { Assert.IsFalse(db.Table<LR2SongDB.song>().Any(row => row.path == removed)); }
                if (laterView)
                {
                    Assert.IsNull(library.DuplicateChartGroups, "別画面では実失効だけを残します。");
                    Assert.IsTrue(normal.IsSelected);
                    Assert.AreSame(normal, tree.SelectedItem);
                    Assert.IsTrue(viewModel.RegularChartList.IsCurrentNavigation(laterNavigation));
                    Assert.AreEqual("same", viewModel.ChartFilters.KeywordFilter);
                    Assert.AreEqual(MainViewUpdateMode.FullScanAllChartsFilterSelected, viewModel.MainChartList.LastAppliedColumnMode);
                }
                else
                {
                    Assert.IsInstanceOfType(tree.SelectedItem, typeof(DuplicateGroup));
                    CollectionAssert.AreEquivalent(next.Folders, ((DuplicateGroup)tree.SelectedItem).Folders);
                    Assert.IsNotNull(library.DuplicateChartGroups);
                }
                Assert.IsFalse(library.OperationAdmission.IsActive);
            }
            finally
            {
                release.Set();
                viewModel.ChartFilters.PropertyChanged -= searchChanged;
                viewModel.MainChartList.PropertyChanged -= rowsChanged;
                tree.SelectedItemChanged -= selectionChanged;
            }
        }, terminal, hashSource: true, fileMutations: mutations);
    }

    /// <summary>成功時の捕捉対象が消えて同見出しの別群だけが残った場合、rootや別群へ切替せず左選択・旧行選択・一覧を解除します。</summary>
    [TestMethod]
    public void DuplicateSelections_DisappearedTargetClearsTreeAndRowsWithoutHeaderFallback()
    {
        var completion = new TaskCompletionSource<DuplicateMaintenanceMutationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        var terminal = new MainWindowDuplicateMaintenanceTerminal((_, _, _) =>
            {
                requests++;
                return completion.Task;
            }, (_, _) => Task.FromResult(DuplicateMaintenanceMutationResult.Rejected(null)), () => true);
        RunDuplicateSelectionFixture((scope, viewModel, window, library, charts, navigation) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
            var root = (TreeViewItem)window.FindName("treeViewItemSearchDuplicated");
            DuplicateGroup remaining = library.DuplicateChartGroups[0];
            ChartFile[] sourceCharts = remaining.Folders.Contains(charts[0].Directory) ? charts[..2] : charts[2..];
            var captured = DuplicateViewContext.CaptureGroup(library.DuplicateChartGroups[1]);
            TreeViewItem source = RealizeDuplicateFolderItem(root, remaining, sourceCharts[0].Directory);
            SelectDuplicateTreeAndAwaitRows(viewModel, window, navigation, () => source.IsSelected = true, sourceCharts[0].Path);
            var table = (CustomTableView)window.FindName("customTableView");
            table.SelectedIndex = 0;
            MenuItem merge = CreateCompiledDuplicateMerge(window, source, sourceCharts[1].Directory);
            Task? handler = null;
            try
            {
                handler = MainWindowPresentationTestHarness.CaptureAsyncEventCompletion(() => RaiseMenuClick(merge));
                Assert.AreEqual(1, requests);
                Assert.IsFalse(completion.Task.IsCompleted);
                library.DuplicateChartGroups = [remaining];
                completion.SetResult(DuplicateMaintenanceMutationResult.Completed(captured));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(handler, "duplicate disappeared selection handler");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsNull(((TreeView)window.FindName("treeView")).SelectedItem);
                Assert.IsFalse(root.IsSelected);
                Assert.AreEqual(0, viewModel.MainChartList.Rows.Count);
                Assert.AreEqual(0, table.GetSelectedRowsSnapshot().Count);
            }
            finally
            {
                completion.TrySetResult(DuplicateMaintenanceMutationResult.Rejected(null));
                if (handler != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(handler, "duplicate disappeared handler cleanup"); }
            }
        }, terminal);
    }

    private static void RunDuplicateSelectionFixture(
        Action<TestWindowPresentationScope, MainWindowViewModel, MainWindow, TestBmsLibrary, ChartFile[], DuplicateSelectionNavigationTasks> test,
        MainWindowDuplicateMaintenanceTerminal? terminal = null, int groupCount = 2, bool hashSource = false,
        OwnedChartCollectionTestSupport.TestFileMutationService? fileMutations = null)
    {
        string directory = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_DuplicateSelection_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TestBmsLibrary? library = null;
        MainWindowViewModel? attachedViewModel = null;
        var navigation = new DuplicateSelectionNavigationTasks();
        var navigationTerminal = new MainWindowMaintenanceTreeTerminal((mode, parameter) =>
            navigation.Record((attachedViewModel ?? throw new AssertFailedException("実owner未接続です。"))
                .RegularChartList.NavigateMaintenanceAsync(mode, parameter)));
        ChartFile[] charts = [];
        Settings settings = TestBmsFactory.CreateRootSettings([directory]);
        settings.StartupSelectInstallPending = false;
        settings.ShowDuplicateFileCheckConfirmMsg = false;
        try
        {
            MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(settings,
                (scope, viewModel, window) =>
                {
                    Assert.IsNotNull(library);
                    window.Width = 900d;
                    window.Height = 700d;
                    test(scope, viewModel, window, library, charts, navigation);
                }, duplicateMaintenanceTerminal: terminal, maintenanceTreeTerminal: navigationTerminal,
                prepareViewModel: viewModel =>
                {
                    attachedViewModel = viewModel;
                    var paths = new List<string>();
                    for (int group = 0; group < groupCount; group++)
                    {
                        for (int member = 0; member < 2; member++)
                        {
                            bool sameFolder = hashSource && group == 0;
                            string folder = Path.Combine(directory, "group" + group + "-" + (sameFolder ? 0 : member));
                            Directory.CreateDirectory(folder);
                            string path = Path.Combine(folder, sameFolder && member == 1 ? "duplicate.bms" : "chart.bms");
                            string title = sameFolder ? "0 cleanup" : "same";
                            File.WriteAllText(path, "#PLAYER 1\n#TITLE " + title + "\n#ARTIST group" + group + "\n#BPM 120\n");
                            paths.Add(path);
                        }
                    }
                    charts = paths.Select(path => BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path))).ToArray();
                    string songDb = Path.Combine(directory, "song.db");
                    BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDb, db =>
                    {
                        db.CreateTable<LR2SongDB.song>();
                        db.CreateTable<LR2SongDB.folder>();
                        db.CreateTable<LR2SongDBExtended.maintenance>();
                    });
                    var gateway = new BmsLibraryDbGateway(songDb);
                    gateway.UpsertSongs(charts);
                    ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(viewModel);
                    // 実store/FS/SQLiteを通し、ごみ箱等のOS操作だけ既存のfixture用境界へ閉じます。
                    library = new TestBmsLibrary(songDb, null, null,
                        fileMutations ?? new OwnedChartCollectionTestSupport.TestFileMutationService(), dialogService: null,
                        uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                        optionsSnapshotProvider: () => BmsLibraryOptionsSnapshot.CreateCurrent(settings),
                        operationAdmission: composition.OperationAdmission,
                        playlistOperationAdmission: composition.PlaylistOperationAdmission, settings: settings)
                    { BmsCharts = charts };
                    library.SearchDuplicateChartGroups();
                    Assert.AreEqual(groupCount, library.DuplicateChartGroups.Count);
                    ((IStartupLibraryApplicationPort)viewModel).AttachStartupLibrary(library);
                    viewModel.MaintenanceTree.AttachLibrary(library);
                });
        }
        finally
        {
            library?.RequestShutdown("duplicate_selection_fixture");
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>実画面の入力から開始した一覧Taskを回収し、期待するパス集合が実表のBindingへ届いたことを確認します。</summary>
    private static void SelectDuplicateTreeAndAwaitRows(MainWindowViewModel viewModel, MainWindow window,
        DuplicateSelectionNavigationTasks navigation, Action action, params string[] expectedPaths)
    {
        SelectDuplicateTreeAndAwaitRows(viewModel, window, navigation, action, expectedPaths, null);
    }

    private static void SelectDuplicateTreeAndAwaitRows(MainWindowViewModel viewModel, MainWindow window,
        DuplicateSelectionNavigationTasks navigation, Action action, string[] expectedPaths, Action<Task>? operationStarted)
    {
        var table = (CustomTableView)window.FindName("customTableView");
        int firstRequest = navigation.Count;
        var firstNavigation = new TaskCompletionSource<Task<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<Task<bool>> started = task => firstNavigation.TrySetResult(task);
        var bound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool observeBinding = false;
        var itemsSource = DependencyPropertyDescriptor.FromProperty(CustomTableView.ItemsSourceProperty, typeof(CustomTableView));
        EventHandler bindingChanged = (_, _) => ObserveBinding();
        PropertyChangedEventHandler rowsChanged = (_, args) =>
        {
            if (args.PropertyName == nameof(MainChartListViewModel.Rows)) { ObserveBinding(); }
        };
        navigation.Started += started;
        itemsSource.AddValueChanged(table, bindingChanged);
        viewModel.MainChartList.PropertyChanged += rowsChanged;
        Task? handler = null;
        try
        {
            handler = MainWindowPresentationTestHarness.CaptureAsyncEventCompletion(action);
            operationStarted?.Invoke(handler);
            TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAny(handler, firstNavigation.Task), "duplicate actual request started");
            if (firstNavigation.Task.IsCompletedSuccessfully)
            {
                // イベントハンドラーが例外を通知済みでも、一覧Taskの失敗と取消を直接観測する。
                TestUiDispatcherHost.AwaitTaskOnDispatcher(firstNavigation.Task.Result, "duplicate first actual navigation terminal");
            }
            TestUiDispatcherHost.AwaitTaskOnDispatcher(handler, "duplicate compiled handler terminal");
            Task<bool>[] requests = navigation.Since(firstRequest);
            Assert.IsTrue(requests.Length > 0, "compiled入力から実ownerの一覧要求が配送されます。");
            TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(requests), "duplicate all actual navigation terminals");
            Assert.IsTrue(requests.All(task => task.Result), "全正規要求が受理されます。表示対象は実Rowsで別に確認します。");
            observeBinding = true;
            ObserveBinding();
            TestUiDispatcherHost.AwaitPresentationOnDispatcher(bound.Task, "duplicate expected rows actual ItemsSource Binding");
            Assert.AreSame(viewModel.MainChartList.Rows, table.ItemsSource);
            CollectionAssert.AreEquivalent(expectedPaths,
                viewModel.MainChartList.Rows.Cast<LibraryChartRow>().Select(row => row.Chart.Path).ToArray());
            CollectionAssert.AreEquivalent(expectedPaths,
                table.ItemsSource.Cast<LibraryChartRow>().Select(row => row.Chart.Path).ToArray());
        }
        finally
        {
            navigation.Started -= started;
            itemsSource.RemoveValueChanged(table, bindingChanged);
            viewModel.MainChartList.PropertyChanged -= rowsChanged;
            try
            {
                if (handler != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(handler, "duplicate compiled handler cleanup"); }
            }
            finally
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(navigation.Since(firstRequest)), "duplicate actual navigation cleanup");
            }
        }

        void ObserveBinding()
        {
            if (!observeBinding || !ReferenceEquals(table.ItemsSource, viewModel.MainChartList.Rows)) { return; }
            string[] paths = table.ItemsSource.Cast<LibraryChartRow>().Select(row => row.Chart.Path).ToArray();
            if (paths.Length == expectedPaths.Length
                && paths.OrderBy(path => path, StringComparer.Ordinal).SequenceEqual(expectedPaths.OrderBy(path => path, StringComparer.Ordinal)))
            { bound.TrySetResult(); }
        }
    }

    /// <summary>正規の画面入力が返すTaskを記録し、自動的なツリー選択で開始した要求も外側のイベント処理と別に回収します。</summary>
    private sealed class DuplicateSelectionNavigationTasks
    {
        private readonly List<Task<bool>> requests = [];

        internal event Action<Task<bool>>? Started;
        internal int Count => requests.Count;

        /// <summary>一覧所有者の実Taskを、そのまま実画面のイベントハンドラーへ返します。</summary>
        internal Task<bool> Record(Task<bool> request)
        {
            requests.Add(request);
            Started?.Invoke(request);
            return request;
        }

        /// <summary>この画面入力から開始した全要求を、失敗と完了の観測へ渡します。</summary>
        internal Task<bool>[] Since(int firstRequest) => requests.Skip(firstRequest).ToArray();
    }

    private static TreeViewItem RealizeDuplicateGroupItem(TreeViewItem root, DuplicateGroup group)
    {
        root.IsExpanded = true;
        root.ApplyTemplate();
        root.Measure(new Size(900d, 700d));
        root.Arrange(new Rect(0d, 0d, 900d, 700d));
        root.UpdateLayout();
        int index = root.Items.IndexOf(group);
        Assert.IsTrue(index >= 0);
        var item = root.ItemContainerGenerator.ContainerFromIndex(index) as TreeViewItem;
        Assert.IsNotNull(item);
        item.IsExpanded = true;
        item.UpdateLayout();
        return item;
    }

    private static TreeViewItem RealizeDuplicateFolderItem(TreeViewItem root, DuplicateGroup group, string folder)
    {
        TreeViewItem parent = RealizeDuplicateGroupItem(root, group);
        var item = parent.ItemContainerGenerator.ContainerFromIndex(parent.Items.IndexOf(folder)) as TreeViewItem;
        Assert.IsNotNull(item);
        return item;
    }

    private static MenuItem CreateCompiledDuplicateMerge(MainWindow window, TreeViewItem source, string destination)
    {
        var menu = (ContextMenu)window.FindResource("treeViewDuplicateFolderContextMenu");
        menu.PlacementTarget = source;
        source.ContextMenu = menu;
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, menu));
        MenuItem parent = menu.Items.OfType<MenuItem>().Single(item => item.Name == "treeViewDuplicateFolderContextMenuItemMergeInto");
        Assert.IsNotNull(parent.ItemContainerStyle);
        parent.ItemsSource = null;
        parent.Items.Clear();
        var command = new MenuItem { DataContext = destination, Tag = source, Style = parent.ItemContainerStyle };
        parent.Items.Add(command);
        return command;
    }

    /// <summary>重複フォルダのcompiledメニューとGキーが元group・移動元先を渡し、単一フォルダでは後片付けへ分岐して完了前にイベントを処理済みにします。</summary>
    [TestMethod]
    public void CompiledDuplicateContextAndShortcutRoutesUseOneTypedTerminal()
    {
        var mergeCalls = new List<(string Source, string Destination, DuplicateGroup Group)>();
        var cleanupCalls = new List<(string Folder, DuplicateGroup Group)>();
        var mergeCompletion = new TaskCompletionSource<DuplicateMaintenanceMutationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var keyboardMergeCompletion = new TaskCompletionSource<DuplicateMaintenanceMutationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupCompletion = new TaskCompletionSource<DuplicateMaintenanceMutationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int mergeInvocation = 0;
        int shortcutProbe = 0;
        var duplicateTerminal = new MainWindowDuplicateMaintenanceTerminal(
            (source, destination, group) =>
            {
                mergeCalls.Add((source, destination, group));
                return Interlocked.Increment(ref mergeInvocation) == 1
                    ? mergeCompletion.Task
                    : keyboardMergeCompletion.Task;
            },
            (group, folder) =>
            {
                cleanupCalls.Add((folder, group));
                return cleanupCompletion.Task;
            },
            () =>
            {
                shortcutProbe++;
                return true;
            });

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (scope, viewModel, window) =>
            {
                try
                {

                    window.Width = 900d;
                    window.Height = 700d;
                    MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
                    ChartFile sourceChart = ChartTestValues.Empty() with
                    {
                        Path = @"C:\wave6e-duplicate\source\chart.bms",
                        Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                        RawTitle = "Duplicate"
                    };
                    var group = new DuplicateGroup(
                        [(sourceChart)],
                        [@"C:\wave6e-duplicate\source", @"C:\wave6e-duplicate\destination"]);
                    var duplicateRoot = (TreeViewItem)window.FindName("treeViewItemSearchDuplicated");
                    duplicateRoot.ItemsSource = new[] { group };
                    duplicateRoot.IsExpanded = true;
                    duplicateRoot.ApplyTemplate();
                    duplicateRoot.Measure(new Size(900d, 700d));
                    duplicateRoot.Arrange(new Rect(0d, 0d, 900d, 700d));
                    duplicateRoot.UpdateLayout();
                    window.UpdateLayout();
                    var groupItem = duplicateRoot.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem;
                    Assert.IsNotNull(
                        groupItem,
                        $"duplicate group container was not generated (items={duplicateRoot.Items.Count}, status={duplicateRoot.ItemContainerGenerator.Status}, visibility={duplicateRoot.Visibility}, expanded={duplicateRoot.IsExpanded}, parent={duplicateRoot.Parent?.GetType().Name ?? "none"})");
                    groupItem!.IsExpanded = true;
                    groupItem!.UpdateLayout();
                    var folderItem = groupItem!.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem;
                    Assert.IsNotNull(folderItem, "duplicate folder container was not generated");
                    Assert.IsNotNull(groupItem!.ItemContainerStyle, "duplicate folder style was not loaded");
                    folderItem!.Style = groupItem!.ItemContainerStyle;
                    folderItem!.ContextMenu = (ContextMenu)window.FindResource("treeViewDuplicateFolderContextMenu");
                    Assert.IsInstanceOfType(folderItem!.DataContext, typeof(string));
                    ContextMenu menu = folderItem!.ContextMenu;
                    menu.PlacementTarget = folderItem!;
                    var mergeMenu = (MenuItem)menu.Items
                        .OfType<MenuItem>()
                        .Single(item => item.Name == "treeViewDuplicateFolderContextMenuItemMergeInto");
                    menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, menu));
                    mergeMenu.ApplyTemplate();
                    mergeMenu.Measure(new Size(400d, 200d));
                    mergeMenu.Arrange(new Rect(0d, 0d, 400d, 200d));
                    mergeMenu.UpdateLayout();
                    Assert.IsNotNull(mergeMenu.ItemContainerStyle, "duplicate merge target style was not loaded");
                    mergeMenu.ItemsSource = null;
                    mergeMenu.Items.Clear();
                    var target = new MenuItem
                    {
                        DataContext = @"C:\wave6e-duplicate\destination",
                        Tag = folderItem!,
                        Style = mergeMenu.ItemContainerStyle
                    };
                    mergeMenu.Items.Add(target);
                    var contextArgs = new RoutedEventArgs(MenuItem.ClickEvent, target);
                    target!.RaiseEvent(contextArgs);
                    Assert.IsTrue(contextArgs.Handled);
                    Assert.IsFalse(mergeCompletion.Task.IsCompleted);
                    Assert.AreEqual(1, mergeCalls.Count);
                    Assert.AreEqual(@"C:\wave6e-duplicate\source", mergeCalls[0].Source);
                    Assert.AreEqual(@"C:\wave6e-duplicate\destination", mergeCalls[0].Destination);
                    Assert.AreSame(group, mergeCalls[0].Group);
                    mergeCompletion.SetResult(DuplicateMaintenanceMutationResult.Rejected(null));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(mergeCompletion.Task, "compiled-terminal-delegate-result");

                    // メニュー結果の適用でTreeViewItemを再利用するため、Gキー操作には現在のcontainerを取り直します。
                    groupItem = duplicateRoot.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem;
                    Assert.IsNotNull(groupItem);
                    groupItem!.IsExpanded = true;
                    groupItem!.UpdateLayout();
                    folderItem = groupItem!.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem;
                    Assert.IsNotNull(folderItem);
                    folderItem!.Style = groupItem!.ItemContainerStyle;
                    folderItem!.ContextMenu = menu;

                    var keyArgs = new KeyEventArgs(
                        Keyboard.PrimaryDevice,
                        PresentationSource.FromVisual(folderItem),
                        0,
                        Key.G)
                    {
                        RoutedEvent = UIElement.KeyDownEvent,
                        Source = folderItem!
                    };
                    Assert.AreEqual(@"C:\wave6e-duplicate\source", folderItem!.DataContext);
                    Assert.AreSame(group, WPFUtil.FindVisualParent<TreeViewItem>(folderItem)?.DataContext);
                    folderItem!.RaiseEvent(keyArgs);
                    Assert.AreEqual(1, shortcutProbe);
                    Assert.IsTrue(keyArgs.Handled);
                    Assert.IsFalse(keyboardMergeCompletion.Task.IsCompleted);
                    Assert.AreEqual(2, mergeCalls.Count);
                    keyboardMergeCompletion.SetResult(DuplicateMaintenanceMutationResult.Rejected(null));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(keyboardMergeCompletion.Task, "compiled-terminal-delegate-result");

                    var singleFolderGroup = new DuplicateGroup(
                        [(sourceChart)],
                        [@"C:\wave6e-duplicate\source"]);
                    duplicateRoot.ItemsSource = new[] { singleFolderGroup };
                    duplicateRoot.IsExpanded = true;
                    duplicateRoot.ApplyTemplate();
                    duplicateRoot.Measure(new Size(900d, 700d));
                    duplicateRoot.Arrange(new Rect(0d, 0d, 900d, 700d));
                    duplicateRoot.UpdateLayout();
                    groupItem = duplicateRoot.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem;
                    Assert.IsNotNull(groupItem);
                    groupItem!.IsExpanded = true;
                    groupItem!.UpdateLayout();
                    folderItem = groupItem!.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem;
                    Assert.IsNotNull(folderItem);
                    folderItem!.ContextMenu = menu;
                    var cleanupArgs = new KeyEventArgs(
                        Keyboard.PrimaryDevice,
                        PresentationSource.FromVisual(folderItem),
                        0,
                        Key.G)
                    {
                        RoutedEvent = UIElement.KeyDownEvent,
                        Source = folderItem!
                    };
                    folderItem!.RaiseEvent(cleanupArgs);
                    Assert.IsTrue(cleanupArgs.Handled);
                    Assert.IsFalse(cleanupCompletion.Task.IsCompleted);
                    Assert.AreEqual(1, cleanupCalls.Count);
                    Assert.AreSame(singleFolderGroup, cleanupCalls[0].Group);
                    Assert.AreEqual(@"C:\wave6e-duplicate\source", cleanupCalls[0].Folder);
                    cleanupCompletion.SetResult(DuplicateMaintenanceMutationResult.Rejected(null));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(cleanupCompletion.Task, "compiled-terminal-delegate-result");
                }
                finally
                {
                    mergeCompletion.TrySetResult(DuplicateMaintenanceMutationResult.Rejected(null));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(mergeCompletion.Task, "compiled-finally-delegate");
                    keyboardMergeCompletion.TrySetResult(DuplicateMaintenanceMutationResult.Rejected(null));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(keyboardMergeCompletion.Task, "compiled-finally-delegate");
                    cleanupCompletion.TrySetResult(DuplicateMaintenanceMutationResult.Rejected(null));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(cleanupCompletion.Task, "compiled-finally-delegate");
                }

            },
            duplicateMaintenanceTerminal: duplicateTerminal);
    }

    /// <summary>選択譜面のcompiled通常・無関係保持推定と導入・解除・削除・修復が元entryまたはchart/tokenを渡し、非同期終端前にイベントを処理済みにします。</summary>
    [TestMethod]
    public void CompiledSelectedChartRoutesPreservePendingTargetAndHandledBeforeCompletion()
    {
        var pendingEstimateCalls = new List<(string Kind, PendingInstallDestinationSearchRequest Request)>();
        var clearPendingCalls = new List<PendingInstallDestinationClearRequest>();
        var installCalls = new List<PendingInstallPackageOperationRequest>();
        var repairCalls = new List<(string Kind, RepairInstalledLocationRequest Request)>();
        var catalogRemovalCalls = new List<PackageCatalogRemovalRequest>();
        var searchInstallCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task searchEvent = Task.CompletedTask;
        var clearCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var forceCompletion = new TaskCompletionSource<PendingPackageMutationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manualCompletion = new TaskCompletionSource<PendingPackageMutationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var repairSearchCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var repairClearCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var repairFixCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalogCompletion = new TaskCompletionSource<PackageCatalogMutationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        ChartFile pendingFile = ChartTestValues.Empty() with
        {
            Path = @"C:\wave6e-pending\chart.bms",
            Md5 = "cccccccccccccccccccccccccccccccc",
            RawTitle = "Pending chart"
        };
        var pendingEntry = PackageChartEntry.FromChart((pendingFile));
        var pendingRow = LibraryChartRow.FromPackageChartEntry(pendingEntry);
        var pendingEstimationTerminal = new MainWindowPendingInstallEstimationTerminal(
            (packages, policy) => Task.CompletedTask,
            request =>
            {
                pendingEstimateCalls.Add(("estimate", request));
                return searchInstallCompletion.Task;
            },
            request =>
            {
                clearPendingCalls.Add(request);
                return clearCompletion.Task;
            },
            _ => Task.CompletedTask);
        var pendingInstallationTerminal = new MainWindowPendingInstallationTerminal(
            _ =>
            {
                return forceCompletion.Task;
            },
            _ => manualCompletion.Task,
            request =>
            {
                installCalls.Add(request);
                return request.Kind == PendingInstallPackageOperationKind.ForceInstall
                    ? forceCompletion.Task
                    : manualCompletion.Task;
            });
        var repairTerminal = new MainWindowInstalledLocationRepairTerminal(
            request =>
            {
                repairCalls.Add(("search", request));
                return repairSearchCompletion.Task;
            },
            request =>
            {
                repairCalls.Add(("clear", request));
                return repairClearCompletion.Task;
            },
            request =>
            {
                repairCalls.Add(("fix", request));
                return repairFixCompletion.Task;
            });
        var catalogTerminal = new MainWindowPackageCatalogTerminal(
            _ => Task.FromResult(PackageCatalogMutationResult.Rejected),
            (_, _) => Task.FromResult(PackageCatalogMutationResult.Rejected),
            request =>
            {
                catalogRemovalCalls.Add(request);
                return catalogCompletion.Task;
            });

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (viewModel, window) =>
            {
                try
                {

                    var table = (CustomTableView)window.FindName("customTableView");
                    table.ItemsSource = new List<object> { pendingRow };
                    table.SelectRowsByPredicate(_ => true);
                    viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.PendingInstallFolderSelected);
                    var tableMenu = (ContextMenu)window.FindResource("tableContextMenu");
                    tableMenu.PlacementTarget = new FrameworkElement { DataContext = pendingRow };

                    MenuItem installGroup = tableMenu.Items
                        .OfType<MenuItem>()
                        .Single(item => item.Items.OfType<MenuItem>().Any(child => Equals(child.Header, Resources.Estimate_install_loc)));
                    string[] installHeaders = [Resources.Estimate_install_loc, Resources.Install_to_estimation, Resources.Force_install, Resources.Clear_estimation];
                    MenuItem[] installCommands = installHeaders.Select(header => installGroup.Items.OfType<MenuItem>().Single(item => Equals(item.Header, header))).ToArray();
                    // 共通化してもメニュー配置元が成立しない3入口は選択済み行だけで実行しません。
                    tableMenu.PlacementTarget = null;
                    foreach (MenuItem command in new[] { installCommands[0], installCommands[1], installCommands[3] })
                    {
                        var invalidArgs = new RoutedEventArgs(MenuItem.ClickEvent, command);
                        Task invalidEvent = MainWindowPresentationTestHarness.CaptureAsyncEventCompletion(() => command.RaiseEvent(invalidArgs));
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(invalidEvent, "compiled-invalid-placement-event");
                        Assert.IsFalse(invalidArgs.Handled);
                    }
                    Assert.AreEqual(0, pendingEstimateCalls.Count);
                    Assert.AreEqual(0, installCalls.Count);
                    Assert.AreEqual(0, clearPendingCalls.Count);
                    tableMenu.PlacementTarget = new FrameworkElement { DataContext = pendingRow };
                    // 明示操作の次の通常操作へ方針が残らず、両方とも元entryを同じ終端へ渡します。
                    foreach (bool keepUnrelated in new[] { true, false })
                    {
                        searchInstallCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        string header = keepUnrelated ? Resources.Estimate_install_loc_keep_unrelated : Resources.Estimate_install_loc;
                        MenuItem searchCommand = installGroup.Items.OfType<MenuItem>().Single(item => Equals(item.Header, header));
                        var searchInstallArgs = new RoutedEventArgs(MenuItem.ClickEvent, searchCommand);
                        searchEvent = MainWindowPresentationTestHarness.CaptureAsyncEventCompletion(() => searchCommand.RaiseEvent(searchInstallArgs));
                        Assert.IsTrue(searchInstallArgs.Handled);
                        Assert.IsFalse(searchInstallCompletion.Task.IsCompleted);
                        Assert.AreEqual(keepUnrelated ? 1 : 2, pendingEstimateCalls.Count);
                        PendingInstallDestinationSearchRequest request = pendingEstimateCalls[^1].Request;
                        Assert.AreEqual(keepUnrelated ? InstallEstimationCandidatePolicy.KeepUnrelated : InstallEstimationCandidatePolicy.RelatedOnly, request.CandidatePolicy);
                        Assert.AreEqual(1, request.PackageTargets.Count);
                        Assert.AreEqual(1, request.SelectedRowCount);
                        Assert.AreSame(pendingEntry, request.PackageTargets[0].PackageEntry);
                        searchInstallCompletion.SetResult(null);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(searchEvent, "compiled-search-event-terminal");
                    }

                    var manualArgs = new RoutedEventArgs(MenuItem.ClickEvent, installCommands[1]);
                    installCommands[1].RaiseEvent(manualArgs);
                    Assert.IsTrue(manualArgs.Handled);
                    Assert.IsFalse(manualCompletion.Task.IsCompleted);
                    Assert.AreEqual(1, installCalls.Count);
                    Assert.AreEqual(PendingInstallPackageOperationKind.ManualInstall, installCalls[^1].Kind);
                    Assert.AreEqual(1, installCalls[^1].Targets.Count);
                    Assert.AreEqual(1, installCalls[^1].SelectedRowCount);
                    Assert.AreSame(pendingEntry, installCalls[^1].Targets[0].PackageEntry);
                    manualCompletion.SetResult(PendingPackageMutationResult.Rejected);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(manualCompletion.Task, "compiled-terminal-delegate-result");

                    var forceArgs = new RoutedEventArgs(MenuItem.ClickEvent, installCommands[2]);
                    installCommands[2].RaiseEvent(forceArgs);
                    Assert.IsTrue(forceArgs.Handled);
                    Assert.IsFalse(forceCompletion.Task.IsCompleted);
                    Assert.AreEqual(2, installCalls.Count);
                    Assert.AreEqual(PendingInstallPackageOperationKind.ForceInstall, installCalls[^1].Kind);
                    Assert.AreEqual(1, installCalls[^1].Targets.Count);
                    Assert.AreEqual(1, installCalls[^1].SelectedRowCount);
                    Assert.AreSame(pendingEntry, installCalls[^1].Targets[0].PackageEntry);
                    forceCompletion.SetResult(PendingPackageMutationResult.Rejected);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(forceCompletion.Task, "compiled-terminal-delegate-result");

                    var clearArgs = new RoutedEventArgs(MenuItem.ClickEvent, installCommands[3]);
                    installCommands[3].RaiseEvent(clearArgs);
                    Assert.IsTrue(clearArgs.Handled);
                    Assert.IsFalse(clearCompletion.Task.IsCompleted);
                    Assert.AreEqual(1, clearPendingCalls.Count);
                    Assert.AreEqual(1, clearPendingCalls[0].PackageTargets.Count);
                    Assert.AreEqual(1, clearPendingCalls[0].PackageTargets.Count + clearPendingCalls[0].LooseTargets.Count);
                    Assert.AreSame(pendingEntry, clearPendingCalls.Single().PackageTargets[0].PackageEntry);
                    clearCompletion.SetResult(null);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(clearCompletion.Task, "compiled-terminal-delegate-result");

                    MenuItem selectedRemoval = tableMenu.Items
                        .OfType<MenuItem>()
                        .Single(item => item.Name == "tableContextMenuItemDeleteInstallPackages");
                    var removalArgs = new RoutedEventArgs(MenuItem.ClickEvent, selectedRemoval);
                    selectedRemoval.RaiseEvent(removalArgs);
                    Assert.IsTrue(removalArgs.Handled);
                    Assert.IsFalse(catalogCompletion.Task.IsCompleted);
                    Assert.AreEqual(1, catalogRemovalCalls.Count);
                    Assert.AreEqual(1, catalogRemovalCalls[0].Targets.Count);
                    Assert.AreSame(pendingEntry, catalogRemovalCalls.Single().Targets[0].PackageEntry);
                    catalogCompletion.SetResult(PackageCatalogMutationResult.Rejected);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(catalogCompletion.Task, "compiled-terminal-delegate-result");

                    ChartFile installedFile = ChartTestValues.Empty() with
                    {
                        Path = @"C:\wave6e-installed\chart.bms",
                        Md5 = "dddddddddddddddddddddddddddddddd",
                        RawTitle = "Installed chart"
                    };
                    var installedRow = LibraryChartRow.FromChartFile(installedFile);
                    table.ItemsSource = new List<object> { installedRow };
                    table.SelectRowsByPredicate(_ => true);
                    viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.FolderFilterSelected);
                    tableMenu.PlacementTarget = new FrameworkElement { DataContext = installedRow };
                    MenuItem repairGroup = tableMenu.Items
                        .OfType<MenuItem>()
                        .Single(item => item.Items.OfType<MenuItem>().Count() == 3);
                    string[] repairHeaders = [Resources.Estimate_reinstall_loc, Resources.Reinstall_to_estimation, Resources.Clear_reinstall_estimation];
                    MenuItem[] repairCommands = repairHeaders.Select(header => repairGroup.Items.OfType<MenuItem>().Single(item => Equals(item.Header, header))).ToArray();
                    var repairSearchArgs = new RoutedEventArgs(MenuItem.ClickEvent, repairCommands[0]);
                    repairCommands[0].RaiseEvent(repairSearchArgs);
                    Assert.IsTrue(repairSearchArgs.Handled);
                    Assert.IsFalse(repairSearchCompletion.Task.IsCompleted);
                    Assert.AreEqual(1, repairCalls.Count);
                    Assert.AreEqual("search", repairCalls.Single().Kind);
                    Assert.AreEqual(1, repairCalls.Single().Request.Targets.Count);
                    Assert.AreSame(installedRow.Chart, repairCalls.Single().Request.Targets[0].Chart);
                    Assert.AreSame(installedFile.Token, repairCalls.Single().Request.Targets[0].Chart.Token);
                    repairSearchCompletion.SetResult(null);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(repairSearchCompletion.Task, "compiled-terminal-delegate-result");

                    var repairFixArgs = new RoutedEventArgs(MenuItem.ClickEvent, repairCommands[1]);
                    repairCommands[1].RaiseEvent(repairFixArgs);
                    Assert.IsTrue(repairFixArgs.Handled);
                    Assert.IsFalse(repairFixCompletion.Task.IsCompleted);
                    Assert.AreEqual(2, repairCalls.Count);
                    Assert.AreEqual("fix", repairCalls[^1].Kind);
                    Assert.AreEqual(1, repairCalls[^1].Request.Targets.Count);
                    Assert.AreSame(installedRow.Chart, repairCalls[^1].Request.Targets[0].Chart);
                    Assert.AreSame(installedFile.Token, repairCalls[^1].Request.Targets[0].Chart.Token);
                    repairFixCompletion.SetResult(null);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(repairFixCompletion.Task, "compiled-terminal-delegate-result");

                    viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.FullScanAllChartsFilterSelected);
                    Assert.AreEqual(1, table.GetSelectedRowsSnapshot().Count);
                    var repairClearArgs = new RoutedEventArgs(MenuItem.ClickEvent, repairCommands[2]);
                    repairCommands[2].RaiseEvent(repairClearArgs);
                    Assert.IsTrue(repairClearArgs.Handled);
                    Assert.IsFalse(repairClearCompletion.Task.IsCompleted);
                    Assert.AreEqual(3, repairCalls.Count);
                    Assert.AreEqual("clear", repairCalls[^1].Kind);
                    Assert.AreEqual(1, repairCalls[^1].Request.Targets.Count);
                    Assert.AreSame(installedRow.Chart, repairCalls[^1].Request.Targets[0].Chart);
                    Assert.AreSame(installedFile.Token, repairCalls[^1].Request.Targets[0].Chart.Token);
                    repairClearCompletion.SetResult(null);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(repairClearCompletion.Task, "compiled-terminal-delegate-result");
                }
                finally
                {
                    searchInstallCompletion.TrySetResult(null);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(searchEvent, "compiled-finally-search-event");
                    clearCompletion.TrySetResult(null);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(clearCompletion.Task, "compiled-finally-delegate");
                    forceCompletion.TrySetResult(PendingPackageMutationResult.Rejected);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(forceCompletion.Task, "compiled-finally-delegate");
                    manualCompletion.TrySetResult(PendingPackageMutationResult.Rejected);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(manualCompletion.Task, "compiled-finally-delegate");
                    repairSearchCompletion.TrySetResult(null);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(repairSearchCompletion.Task, "compiled-finally-delegate");
                    repairClearCompletion.TrySetResult(null);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(repairClearCompletion.Task, "compiled-finally-delegate");
                    repairFixCompletion.TrySetResult(null);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(repairFixCompletion.Task, "compiled-finally-delegate");
                    catalogCompletion.TrySetResult(PackageCatalogMutationResult.Rejected);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(catalogCompletion.Task, "compiled-finally-delegate");
                }

            },
            packageCatalogTerminal: catalogTerminal,
            pendingInstallEstimationTerminal: pendingEstimationTerminal,
            pendingInstallationTerminal: pendingInstallationTerminal,
            installedLocationRepairTerminal: repairTerminal);
    }

    private static MenuItem FindMenuItem(ItemsControl root, string name)
    {
        foreach (MenuItem item in root.Items.OfType<MenuItem>())
        {
            if (item.Name == name)
            {
                return item;
            }
            if (item.Items.Count > 0)
            {
                MenuItem nested = FindMenuItem(item, name);
                if (nested != null)
                {
                    return nested;
                }
            }
        }
        throw new AssertFailedException($"Menu item '{name}' was not found.");
    }

    /// <summary>カタログ・保留メニューの同期例外と待機後のTask失敗を一回の一般通知で扱い、通知失敗でも操作を再実行せず実イベントの終端を待ちます。</summary>
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void CompiledPackageMenusHandleSynchronousAndAwaitedFaultsOnce(bool pendingBulk, bool synchronousThrow)
    {
        var failure = new IOException("compiled package operation marker");
        var releaseOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var noticeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNotice = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogs = new FileDbReportRecordingDialogs
        {
            MessageHandler = _ =>
            {
                noticeStarted.TrySetResult();
                return releaseNotice.Task;
            }
        };
        int calls = 0;
        PackageCatalogSection? capturedSection = null;
        async Task<PackageCatalogMutationResult> CatalogFaultAsync()
        {
            await releaseOperation.Task;
            throw failure;
        }
        async Task BulkFaultAsync()
        {
            await releaseOperation.Task;
            throw failure;
        }
        var catalog = new MainWindowPackageCatalogTerminal(
            section =>
            {
                calls++;
                capturedSection = section;
                if (synchronousThrow) { throw failure; }
                return CatalogFaultAsync();
            },
            (_, _) => Task.FromResult(PackageCatalogMutationResult.Rejected),
            _ => Task.FromResult(PackageCatalogMutationResult.Rejected));
        var bulk = new MainWindowPendingBulkMaintenanceTerminal(
            () =>
            {
                calls++;
                if (synchronousThrow) { throw failure; }
                return BulkFaultAsync();
            },
            () => Task.CompletedTask,
            () => Task.CompletedTask);
        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (_, window) =>
            {
                var menu = (ContextMenu)window.FindResource("treeViewInstallPendingContextMenu");
                MenuItem item = pendingBulk
                    ? menu.Items.OfType<MenuItem>().Last().Items.OfType<MenuItem>().First()
                    : menu.Items.OfType<MenuItem>().First();
                Task terminal = MainWindowPresentationTestHarness.CaptureAsyncEventCompletion(() => RaiseMenuClick(item));
                try
                {
                    Assert.AreEqual(1, calls);
                    if (!pendingBulk) { Assert.AreEqual(PackageCatalogSection.Pending, capturedSection); }
                    if (!synchronousThrow)
                    {
                        Assert.AreEqual(0, dialogs.Messages.Count);
                        Assert.IsFalse(terminal.IsCompleted);
                    }
                    releaseOperation.TrySetResult();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        TestUiDispatcherHost.AwaitNotificationAsync(noticeStarted.Task, terminal, "compiled package failure notice"),
                        "compiled package notice-or-terminal");
                    Assert.AreEqual(1, dialogs.Messages.Count);
                    Assert.AreEqual(Resources.Msg_error_unexpected, dialogs.Messages[0].MessageBoxText);
                    Assert.AreEqual(MessageBoxImage.Hand, dialogs.Messages[0].Icon);
                    Assert.AreSame(window, dialogs.Messages[0].Owner);
                    Assert.IsFalse(terminal.IsCompleted, "イベント終端は通知の実待機を所有します。");
                    releaseNotice.TrySetResult(UiDialogResult.NotShown(UiDialogStatus.Failed));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(terminal, "compiled package terminal");
                    Assert.AreEqual(1, calls);
                    Assert.AreEqual(1, dialogs.Messages.Count);
                }
                finally
                {
                    releaseOperation.TrySetResult();
                    releaseNotice.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(terminal, "compiled package finally terminal");
                }
            },
            packageCatalogTerminal: catalog,
            pendingBulkMaintenanceTerminal: bulk,
            playlistWorkspaceDialogService: dialogs);
    }

    private static void RaiseMenuClick(MenuItem item)
        => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
}

internal static class MainWindowPackageMaintenanceTestHarness
{
    /// <summary>実shutdown/Closedと設定復元を回収し、表示する親Windowは同じ外側scopeへ追跡します。</summary>
    /// <param name="prepareWindowForPresentation">親Windowを表示するfixtureだけが、表示前に所有するscopeへ準備を接続します。</param>
    /// <param name="installTreeTerminal">保留ツリーの実選択と表示反映を、導入表示構築から分けて観測する場合の終端です。</param>
    /// <param name="uiScheduler">UI配送と開始済みTaskを観測するスケジューラーです。省略時は既存の配送処理を使います。</param>
    internal static void RunConstructorOnly(
        Settings settings,
        Action<MainWindowViewModel, MainWindow> test,
        MainWindowFolderAutoRenameTerminal? folderAutoRenameTerminal = null,
        MainWindowDuplicateMaintenanceTerminal? duplicateMaintenanceTerminal = null,
        MainWindowMaintenanceRescanTerminal? maintenanceRescanTerminal = null,
        MainWindowMaintenanceTreeTerminal? maintenanceTreeTerminal = null,
        MainWindowPackageCatalogTerminal? packageCatalogTerminal = null,
        MainWindowPendingInstallEstimationTerminal? pendingInstallEstimationTerminal = null,
        MainWindowPendingInstallationTerminal? pendingInstallationTerminal = null,
        MainWindowInstalledLocationRepairTerminal? installedLocationRepairTerminal = null,
        MainWindowPendingBulkMaintenanceTerminal? pendingBulkMaintenanceTerminal = null,
        MainWindowMainChartCellEditTerminal? mainChartCellEditTerminal = null,
        MainWindowSelectedChartContextMenuTerminals? selectedChartContextMenuTerminals = null,
        MainWindowPlaybackTerminal? playbackTerminal = null,
        MainWindowPlaylistWorkspaceTerminals? playlistWorkspaceTerminals = null,
        MainWindowPendingPackageMutationViewTerminal? pendingPackageMutationViewTerminal = null,
        Action<MainWindowViewModel>? prepareViewModel = null,
        IUiDialogService? playlistWorkspaceDialogService = null,
        Action<TestWindowPresentationScope, MainWindow>? prepareWindowForPresentation = null,
        Func<TestWindowPresentationScope, Window, ChartPropertiesWindowManager>? chartPropertiesManagerFactory = null,
        MainWindowInstallTreeTerminal? installTreeTerminal = null, IUiScheduler? uiScheduler = null)
        => RunConstructorOnly(settings, (_, viewModel, window) => test(viewModel, window),
            folderAutoRenameTerminal, duplicateMaintenanceTerminal, maintenanceRescanTerminal, maintenanceTreeTerminal, packageCatalogTerminal, pendingInstallEstimationTerminal, pendingInstallationTerminal, installedLocationRepairTerminal, pendingBulkMaintenanceTerminal, mainChartCellEditTerminal, selectedChartContextMenuTerminals, playbackTerminal, playlistWorkspaceTerminals, pendingPackageMutationViewTerminal, prepareViewModel, playlistWorkspaceDialogService, prepareWindowForPresentation, chartPropertiesManagerFactory, installTreeTerminal, uiScheduler);

    /// <summary>親子Windowに同じ表示scopeを渡し、実MainWindow寿命を共通補助で回収します。</summary>
    /// <param name="installTreeTerminal">保留ツリーの実選択と表示反映を、導入表示構築から分けて観測する場合の終端です。</param>
    /// <param name="uiScheduler">UI配送と開始済みTaskを観測するスケジューラーです。省略時は既存の配送処理を使います。</param>
    internal static void RunConstructorOnly(
        Settings settings,
        Action<TestWindowPresentationScope, MainWindowViewModel, MainWindow> test,
        MainWindowFolderAutoRenameTerminal? folderAutoRenameTerminal = null,
        MainWindowDuplicateMaintenanceTerminal? duplicateMaintenanceTerminal = null,
        MainWindowMaintenanceRescanTerminal? maintenanceRescanTerminal = null,
        MainWindowMaintenanceTreeTerminal? maintenanceTreeTerminal = null,
        MainWindowPackageCatalogTerminal? packageCatalogTerminal = null,
        MainWindowPendingInstallEstimationTerminal? pendingInstallEstimationTerminal = null,
        MainWindowPendingInstallationTerminal? pendingInstallationTerminal = null,
        MainWindowInstalledLocationRepairTerminal? installedLocationRepairTerminal = null,
        MainWindowPendingBulkMaintenanceTerminal? pendingBulkMaintenanceTerminal = null,
        MainWindowMainChartCellEditTerminal? mainChartCellEditTerminal = null,
        MainWindowSelectedChartContextMenuTerminals? selectedChartContextMenuTerminals = null,
        MainWindowPlaybackTerminal? playbackTerminal = null,
        MainWindowPlaylistWorkspaceTerminals? playlistWorkspaceTerminals = null,
        MainWindowPendingPackageMutationViewTerminal? pendingPackageMutationViewTerminal = null,
        Action<MainWindowViewModel>? prepareViewModel = null,
        IUiDialogService? playlistWorkspaceDialogService = null,
        Action<TestWindowPresentationScope, MainWindow>? prepareWindowForPresentation = null,
        Func<TestWindowPresentationScope, Window, ChartPropertiesWindowManager>? chartPropertiesManagerFactory = null,
        MainWindowInstallTreeTerminal? installTreeTerminal = null, IUiScheduler? uiScheduler = null)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var lifetime = new MainWindowPresentationTestHarness.PresentationApplicationLifetime();
            MainWindowViewModel viewModel = new ApplicationComposition(
                settingsEditSession: new NoOpSettingsEditSession(settings),
                uiScheduler: uiScheduler ?? new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                applicationLifetime: lifetime,
                cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                playlistWorkspaceDialogService: playlistWorkspaceDialogService)
                .CreateMainWindowViewModelForTest();
            var ownership = new MainWindowTestLifetime(viewModel, lifetime.ShutdownRequested.Task);
            ownership.Run(() =>
            {
                MainWindowPresentationTestHarness.PrepareConstructorOnlyShell(viewModel);
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(false);
                prepareViewModel?.Invoke(viewModel);
                MainWindow window = ownership.CreateWindow(() => new MainWindow(
                    viewModel,
                    settingsWindowCreated: null,
                    libraryReloadMenuTerminal: null,
                    regularLibraryTreeTerminal: null,
                    maintenanceTreeTerminal: maintenanceTreeTerminal,
                    installTreeTerminal: installTreeTerminal,
                    zeroNoteRecheckTerminal: null,
                    columnResetTerminal: null,

                    folderAutoRenameTerminal,
                    duplicateMaintenanceTerminal,
                    maintenanceRescanTerminal,
                    packageCatalogTerminal,
                    pendingInstallEstimationTerminal,
                    pendingInstallationTerminal,
                    installedLocationRepairTerminal,
                    pendingBulkMaintenanceTerminal,
                    mainChartCellEditTerminal,
                    selectedChartContextMenuTerminals,
                    playbackTerminal,
                    playlistWorkspaceTerminals,
                    pendingPackageMutationViewTerminal: pendingPackageMutationViewTerminal,
                    playlistWorkspaceDialogService: playlistWorkspaceDialogService,
                    chartPropertiesManagerFactory: chartPropertiesManagerFactory == null ? null : owner => chartPropertiesManagerFactory(windowTest, owner)));
                prepareWindowForPresentation?.Invoke(windowTest, window);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                test(windowTest, viewModel, window);
            });
        });
    }
}
