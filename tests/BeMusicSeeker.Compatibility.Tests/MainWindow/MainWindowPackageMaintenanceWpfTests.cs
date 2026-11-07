using System.Windows;
using System.Windows.Input;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using static BeMusicSeeker.TestSupport.Wpf.PackageMaintenanceEditorTestSupport;

namespace BeMusicSeeker.Tests;


/// <summary>保守操作の実ファイル・DB反映と、compiledメニューから管理主体への接続を検証します。</summary>
/// <remarks>要求の可否とreceipt単体は下位へ任せ、ここでは実画面の選択・編集保持やクリックの配送先と非同期終端を観測します。</remarks>
[TestClass]
[TestCategory("Suite.DriveRoot")]
// 実画面の共有Resourcesと音声資源を、終了まで同じfixtureで所有します。
[DoNotParallelize]
public sealed partial class MainWindowPackageMaintenanceWpfTests
{

    /// <summary>候補の改名・別登録rootへの移動でdrive root直下の所持譜面を保持し、compiled導入先セルの確定・再編集・取消で末尾separatorを失いません。</summary>
    /// <remarks>ドライブ別名を必要とする二つの接続保証だけを専用環境で実行します。推定分類・健康度・通常編集は通常tempのケースと下位条件表へ分担します。</remarks>
    [TestMethod]
    [TestCategory("EnvironmentIntegration")]
    public void PendingDestinationEditing_DriveRootPreservesOwnedChartThroughRelocationAndReedit()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = workspace.RootDirectory;
        string previousDirectory = Environment.CurrentDirectory;
        OwnedTestDrive? drive = null;
        TestBmsLibrary? library = null;
        Exception? primaryFailure = null;
        try
        {
            drive = OwnedTestDrive.Create(root, 'Z');
            drive.AssertOwned();
            string absoluteRoot = drive.Root;
            string registeredRoot = Path.Combine(absoluteRoot, "LibraryRoot");
            string candidate = Path.Combine(registeredRoot, "Candidate");
            string destinationRoot = Path.Combine(absoluteRoot, "OtherRoot");
            string source = Path.Combine(root, "Pending");
            foreach (string directory in new[] { candidate, destinationRoot, source }) { Directory.CreateDirectory(directory); }
            string candidatePath = Path.Combine(candidate, "candidate.bms");
            string rootOwnedPath = Path.Combine(absoluteRoot, "root-owned.bms");
            string sourcePath = Path.Combine(source, "pending.bms");
            File.WriteAllText(candidatePath, "#TITLE Candidate\r\n#ARTIST Candidate Artist\r\n");
            File.WriteAllText(rootOwnedPath, "#TITLE Root Owned\r\n#ARTIST Root Artist\r\n");
            File.WriteAllText(sourcePath, "#TITLE Pending\r\n#ARTIST Pending Artist\r\n");
            byte[] rootOwnedBytes = File.ReadAllBytes(rootOwnedPath);
            var entry = PackageChartEntry.FromPath(sourcePath);
            var package = ChartPackage.FromChartEntries([entry]);
            package.path = source;
            string songDbPath = Path.Combine(root, "song.db");
            File.WriteAllBytes(songDbPath, []);
            var scanner = CapturedChartFileScanner.FromFixture([candidatePath, rootOwnedPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                { [absoluteRoot] = [], [registeredRoot] = [], [candidate] = [], [destinationRoot] = [] },
                [absoluteRoot, destinationRoot]);
            var options = new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false, ScanBmsFilesOnStartup = true };
            Settings settings = TestSettingsFactory.CreateIsolatedSettings(values => values.StartupSelectInstallPending = false);
            // D:\がD:へ変わると同driveのこの子ディレクトリへ解決されるため、誤変換を観測できます。
            Environment.CurrentDirectory = Directory.CreateDirectory(Path.Combine(absoluteRoot, "Working")).FullName;
            MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(settings, (scope, viewModel, window) =>
            {
                TestBmsLibrary currentLibrary = library ?? throw new AssertFailedException("準備済みライブラリが必要です。");
                ChartFile rootOwnedBefore = currentLibrary.BmsCharts.Single(chart => chart.Path == rootOwnedPath);
                MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    viewModel.RegularChartList.NavigateInstallAsync(MainViewUpdateMode.PendingInstallFolderSelected), "drive root pending navigation");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                var table = (CustomTableView)window.FindName("customTableView");
                ChangeCandidate(() => currentLibrary.RenameChartFolder(candidate, "Renamed"), Path.Combine(registeredRoot, "Renamed"));
                LibraryChartRef[] movingCharts = [.. currentLibrary.BmsCharts
                    .Where(chart => string.Equals(Path.GetDirectoryName(chart.Path), candidate, StringComparison.OrdinalIgnoreCase))
                    .Select(chart => LibraryChartRef.FromChartFile(chart))];
                Assert.AreEqual(1, movingCharts.Length);
                ChangeCandidate(() => currentLibrary.MoveLibraryRootFolder(movingCharts, destinationRoot), Path.Combine(destinationRoot, "Renamed"));

                table.Width = 900d; table.Height = 220d; table.HeaderHeight = 0d; table.RowHeight = 70d;
                viewModel.MainChartList.ColumnsSettings.InstallDst.Visibility = Visibility.Visible;
                CustomTableColumn destinationColumn = table.Columns.Single(column => column.EditPropertyName == "instl_dst");
                table.Columns = [destinationColumn];
                SelectDestinationCell();
                Assert.IsTrue(table.HandleKeyDown(Key.F2, ModifierKeys.None));
                FindCurrentEditors(table).Single().Text = absoluteRoot;
                ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(viewModel);
                var mutation = new TaskCompletionSource<PendingPackageMutationAppliedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnMutation(object? sender, PendingPackageWorkflowChangedEventArgs args)
                { if (args is PendingPackageMutationAppliedEventArgs applied) { mutation.TrySetResult(applied); } }
                Task? admissionTerminal = null;
                Exception? editFailure = null;
                viewModel.PendingPackages.WorkflowChanged += OnMutation;
                try
                {
                    Assert.IsTrue(table.HandleKeyDown(Key.Return, ModifierKeys.None));
                    admissionTerminal = composition.OperationAdmission.WaitForIdleAsync();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        TestUiDispatcherHost.AwaitNotificationAsync(mutation.Task, admissionTerminal, "drive root mutation"), "drive root mutation");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(mutation.Task.GetAwaiter().GetResult().Presentation, "drive root presentation");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(admissionTerminal, "drive root admission terminal");
                }
                catch (Exception failure) { editFailure = failure; throw; }
                finally
                {
                    var failures = new List<Exception>();
                    try
                    {
                        if (mutation.Task.IsCompletedSuccessfully)
                        { TestUiDispatcherHost.AwaitTaskOnDispatcher(mutation.Task.Result.Presentation, "drive root presentation cleanup"); }
                    }
                    catch (Exception failure) { failures.Add(failure); }
                    try
                    {
                        if (admissionTerminal != null)
                        { TestUiDispatcherHost.AwaitTaskOnDispatcher(admissionTerminal, "drive root admission cleanup"); }
                    }
                    catch (Exception failure) { failures.Add(failure); }
                    finally { viewModel.PendingPackages.WorkflowChanged -= OnMutation; }
                    if (failures.Count != 0)
                    {
                        if (editFailure != null) { failures.Insert(0, editFailure); }
                        throw new AggregateException(failures);
                    }
                }
                Assert.AreEqual(absoluteRoot, entry.Chart.InstallDestination);
                Assert.AreEqual(absoluteRoot, CurrentRow().instl_dst);
                SelectDestinationCell();
                Assert.IsTrue(table.HandleKeyDown(Key.F2, ModifierKeys.None));
                Assert.AreEqual(absoluteRoot, FindCurrentEditors(table).Single().Text);
                Assert.IsTrue(table.HandleKeyDown(Key.Escape, ModifierKeys.None));
                table.HandleKeyDown(Key.Escape, ModifierKeys.None);
                Assert.AreEqual(0, FindCurrentEditors(table).Count());
                Assert.AreEqual(absoluteRoot, entry.Chart.InstallDestination);
                Assert.AreEqual(absoluteRoot, CurrentRow().instl_dst);
                Assert.IsFalse(composition.OperationAdmission.IsActive);
                AssertRootOwned();

                LibraryChartRow CurrentRow() => table.ItemsSource.Cast<LibraryChartRow>().Single(row => row.path == sourcePath);
                void SelectDestinationCell()
                {
                    Assert.AreSame(viewModel.MainChartList.Rows, table.ItemsSource);
                    table.Measure(new Size(table.Width, table.Height));
                    table.Arrange(new Rect(0d, 0d, table.Width, table.Height));
                    table.UpdateLayout();
                    table.SelectRowsByPredicate(value => value is LibraryChartRow row && row.path == sourcePath);
                    Assert.IsTrue(table.SelectedIndex >= 0);
                    Assert.IsTrue(table.IsCurrentCell(table.SelectedIndex, destinationColumn));
                }
                void AssertRootOwned()
                {
                    CollectionAssert.AreEqual(rootOwnedBytes, File.ReadAllBytes(rootOwnedPath));
                    ChartFile current = currentLibrary.BmsCharts.Single(chart => ReferenceEquals(chart.Token, rootOwnedBefore.Token));
                    Assert.AreEqual(rootOwnedPath, current.Path);
                    Assert.AreSame(rootOwnedBefore.Token, current.Token);
                    using LR2SongDBExtended readback = new BmsLibraryDbGateway(currentLibrary.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
                    Assert.AreEqual(rootOwnedPath, readback.Table<LR2SongDB.song>().Single(chart => chart.path == rootOwnedPath).path);
                }
                void ChangeCandidate(Action operation, string nextCandidate)
                {
                    var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    void OnRefresh(object? sender, NormalLibraryRefreshAppliedEventArgs args) => refreshed.TrySetResult();
                    viewModel.RegularChartList.NormalLibraryRefreshApplied += OnRefresh;
                    try
                    {
                        operation();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(refreshed.Task, "drive root candidate relocation presentation");
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(MainWindowViewModelTestFactory.GetComposition(viewModel).OperationAdmission.WaitForIdleAsync(), "drive root candidate relocation terminal");
                        RegularChartNavigationTestFixture.AwaitViewPreparationsOnDispatcher(viewModel.RegularChartList, "drive root candidate relocation view preparation");
                        Assert.IsFalse(Directory.Exists(candidate));
                        Assert.IsTrue(File.Exists(Path.Combine(nextCandidate, "candidate.bms")));
                        candidate = nextCandidate;
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        AssertRootOwned();
                    }
                    finally { viewModel.RegularChartList.NormalLibraryRefreshApplied -= OnRefresh; }
                }
            }, prepareViewModel: viewModel =>
            {
                ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(viewModel);
                library = new TestBmsLibrary(composition.DatabaseScope, songDbPath, getLR2Config: null, _lr2ScoreDB: null, startupRequiredFileScanReason: null,
                    optionsSnapshotProvider: () => options, applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
                    chartFileScanner: scanner, operationAdmission: composition.OperationAdmission, playlistOperationAdmission: composition.PlaylistOperationAdmission,
                    settings: TestBmsFactory.CreateRootSettings([absoluteRoot, destinationRoot]))
                { StartupBackgroundTaskScheduler = (_, _, _, _) => false };
                library.Initialize(null, null, BMSLibrary.LibraryInitializeMode.Startup);
                library.ChartPackagesPending = [package];
                BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(library.DatabaseScope, songDbPath, db =>
                {
                    db.CreateTable<LR2SongDBExtended.install>();
                    db.InsertOrReplace(package, typeof(LR2SongDBExtended.install));
                });
                ((IStartupLibraryApplicationPort)viewModel).AttachStartupLibrary(library);
            });
        }
        catch (Exception failure) { primaryFailure = failure; throw; }
        finally
        {
            var failures = new List<Exception>();
            try { library?.RequestShutdown("pending-drive-root-presentation-test"); }
            catch (Exception failure) { failures.Add(failure); }
            try { Environment.CurrentDirectory = previousDirectory; }
            catch (Exception failure) { failures.Add(failure); }
            if (failures.Count == 0)
            {
                try { drive?.AssertOwned(); drive?.Dispose(); }
                catch (Exception failure) { failures.Add(failure); }
            }
            if (failures.Count != 0) { workspace.PreserveArtifacts(); }
            if (failures.Count != 0)
            {
                if (primaryFailure != null) { failures.Insert(0, primaryFailure); }
                throw new AggregateException("未回収のdrive fixtureを保持します: " + root + "; " + drive, failures);
            }
        }
    }

}
