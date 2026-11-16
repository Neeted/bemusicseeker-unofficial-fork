using System.Collections.Concurrent;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.TestSupport.Components.Lr2SongDbSyncTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class InstallableMaintenanceAdmissionTests
{
    /// <summary>
    /// 保証：LR2同期が受付を保持する間は受理済み保守を待たせ、同期後に両方の実DB更新を順に公開して次の明示同期も受理する。
    /// </summary>
    /// <remarks>実初期化から保守の生産・scheduler接続・LR2worker・DB確定まで通し、手動通知では分からない更新欠落と受付の早期解放を確認する。</remarks>
    [TestMethod]
    public async Task QueuedLr2AndAcceptedMaintenance_WaitForOneAdmissionAndPublishBothActualUpdates()
    {

        using var scope = TestDatabaseScope.Create();
        ApplicationDatabaseScope databaseScope = scope.DatabaseScope;
        string root = Path.Combine(scope.DirectoryPath, "BMS");
        Directory.CreateDirectory(root);
        string chart = Path.Combine(root, "chart.bms");
        WriteBasicBms(chart, "Song");
        File.WriteAllBytes(Path.Combine(root, "sound.wav"), [1]);
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chart);
        using (LR2SongDBExtended db = databaseScope.OpenSongWrite(scope.SongDbPath))
        {
            db.CreateTable<LR2SongDB.song>();
            db.CreateTable<LR2SongDB.folder>();
            db.CreateTable<LR2SongDBExtended.maintenance>();
            db.InsertOrReplace(new LR2SongDB.song { path = chart, hash = snapshot.Md5, title = "Song" });
        }
        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = true,
            LR2RootPath = scope.DirectoryPath,
            ScanBmsFilesOnStartup = false
        };
        string generatedFolderPath = Path.Combine(root, "manual.lr2folder");
        File.WriteAllText(generatedFolderPath, "#TITLE Manual actual sync", System.Text.Encoding.GetEncoding("shift_jis"));
        var preparationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releasePreparation = new ManualResetEventSlim();
        var backgroundStarted = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fullyIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new ConcurrentBag<Task>();
        var publicationOrder = new ConcurrentQueue<string>();
        var library = new TestBmsLibrary(databaseScope, scope.SongDbPath, null, null, null, () => options,
            TestBmsFactory.MissingEverythingBridge,
            CapturedChartFileScanner.FromFixture([chart], new Dictionary<string, IEnumerable<string>>
            {
                [root] = ["sound.wav"]
            }, [root]), new TestUiScheduler(() => null), new EmptyRootFileEnumerator(), settings: TestBmsFactory.CreateRootSettings([root], lr2: true))
        { };
        StartupBackgroundTaskSchedulerOwner? scheduler = null;
        scheduler = new StartupBackgroundTaskSchedulerOwner(() => library.IsShutdownRequested,
            _ => { }, _ => { }, _ => { }, text => text,
            (_, _) => { if (scheduler?.IsFullyIdle == true) { fullyIdle.TrySetResult(); } }, new object());
        library.StartupBackgroundTaskScheduler = (name, reason, dependency, work) =>
            scheduler.Queue(name, reason, dependency, async () =>
            {
                try
                {
                    Task actual = work();
                    tasks.Add(actual);
                    if (name is "maintenance_hydration" or "installable_maintenance") { backgroundStarted.TrySetResult(actual); }
                    await actual;
                }
                catch (Exception failure) { backgroundStarted.TrySetException(failure); throw; }
            });
        library.PropertyChanged += ObservePublication;
        void ObservePublication(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(BMSLibrary.Lr2SongDbSyncCompletedVersion)) { publicationOrder.Enqueue("lr2"); }
            if (args.PropertyName == nameof(BMSLibrary.InstallableMaintenanceDeferredCompletedVersion)) { publicationOrder.Enqueue("maintenance"); }
        }
        Task<Lr2SongDbSyncStatusSnapshot>? lr2Operation = null;
        try
        {
            library.Initialize(null, null, BMSLibrary.LibraryInitializeMode.Startup);
            int maintenanceVersion = library.InstallableMaintenanceDeferredRequestedVersion;
            Assert.IsTrue(maintenanceVersion > 0, "実初期化が必須maintenanceを受理します。");
            lr2Operation = library.QueueLr2SongDbSyncAsync("maintenance-admission-crossing", force: true,
                (_, _) =>
                {
                    preparationEntered.TrySetResult();
                    releasePreparation.Wait();
                    return new Lr2SongDbSyncPreparedDataSurface([root], [generatedFolderPath],
                        new Dictionary<string, RootFileEnumerationEntry>
                        { [generatedFolderPath] = new(generatedFolderPath, File.GetLastWriteTimeUtc(generatedFolderPath)) }, true);
                });
            await Task.WhenAny(preparationEntered.Task, lr2Operation);
            if (!preparationEntered.Task.IsCompleted) { await lr2Operation; Assert.Fail("実準備が開始されませんでした。"); }
            scheduler.MarkRequiredInitializationSchedulingComplete();
            scheduler.MarkPostInitializationSchedulingComplete();
            scheduler.Start();
            await Task.WhenAny(backgroundStarted.Task, lr2Operation, fullyIdle.Task);
            if (!backgroundStarted.Task.IsCompleted) { await lr2Operation; Assert.Fail("受理済み保守が開始されませんでした。"); }
            Task background = await backgroundStarted.Task;
            Assert.IsFalse(background.IsCompleted, "実post枠で開始した必須保守はLR2の共通受付終端を待ちます。");
            Assert.IsFalse(lr2Operation.IsCompleted);
            Assert.IsTrue(library.OperationAdmission.IsActive);
            Assert.IsFalse(library.OperationAdmission.TryEnter(out IDisposable rejected));
            rejected?.Dispose();
            Assert.AreEqual(0, library.InstallableMaintenanceDeferredCompletedVersion);
            using (LR2SongDBExtended before = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly())
            { Assert.IsNull(before.Find<LR2SongDB.folder>(generatedFolderPath)); }

            releasePreparation.Set();
            await lr2Operation;
            var actualBackground = Task.WhenAll(tasks);
            await Task.WhenAny(actualBackground, fullyIdle.Task);
            await actualBackground;
            await fullyIdle.Task;
            Assert.AreEqual(maintenanceVersion, library.InstallableMaintenanceDeferredCompletedVersion);
            Assert.AreEqual(library.Lr2SongDbSyncRequestedVersion, library.Lr2SongDbSyncCompletedVersion);
            Assert.IsFalse(library.OperationAdmission.IsActive);
            Assert.IsFalse(library.InstallableMaintenanceDeferredRunning);
            CollectionAssert.AreEqual(new[] { "lr2", "maintenance" }, publicationOrder.ToArray());
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual("Completed", verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName).status);
                Assert.AreEqual("Manual actual sync", verify.Find<LR2SongDB.folder>(generatedFolderPath).title,
                    "同期workerが実folder表を更新したことを確認します。");
                Assert.IsTrue(verify.Table<LR2SongDBExtended.maintenance>().Any(row => row.path == chart),
                    "受付済みの実maintenance更新を捨てずにDBへ反映します。");
            }
            Assert.IsTrue(library.OperationAdmission.TryEnter(out IDisposable next));
            next.Dispose();
            lr2Operation = library.QueueLr2SongDbSyncAsync("after-maintenance-explicit", force: true);
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed, (await lr2Operation).Status,
                "両更新の終端後、次の明示同期も実workerを完了できます。");
            Assert.AreEqual(library.Lr2SongDbSyncRequestedVersion, library.Lr2SongDbSyncCompletedVersion);
            Assert.IsFalse(library.OperationAdmission.IsActive);
        }
        finally
        {
            releasePreparation.Set();
            try { if (lr2Operation != null) { await lr2Operation; } }
            finally
            {
                scheduler.MarkRequiredInitializationSchedulingComplete();
                scheduler.MarkPostInitializationSchedulingComplete();
                scheduler.Start();
                try { if (!scheduler.IsFullyIdle) { await fullyIdle.Task; } }
                finally
                {
                    try { await Task.WhenAll(tasks); }
                    finally
                    {
                        library.PropertyChanged -= ObservePublication;
                        library.RequestShutdown("maintenance-admission-test");
                    }
                }
            }
        }
    }
    /// <summary>
    /// 起動後の保守が集めた読込み失敗は、読取り・確定・保守の受付の保持中には表示せず、受付の解放後に一度だけまとめて投函する。
    /// 保守が例外で終わっても投函し、表示が終わらなくても保守の終端と次の受付を待たせない。
    /// 受付の保持中の表示（同期の表示の残存）、受付の解放前の投函、例外の経路での通知の喪失、表示の完了待ちによる保守の停止を検出する。
    /// </summary>
    /// <remarks>
    /// 実ライブラリで保守を捕捉して実行し、読めない譜面二件と読める譜面一件をDBへ置く。例外の形は、保守の保存をSQLiteのトリガーで拒否して作る。
    /// 窓口は実の <see cref="BmsLibraryDialogService"/> で、表示先は表示を完了しない記録用の窓口。要求の時点の共通受付の取得可否を記録する。
    /// </remarks>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InstallableMaintenance_PostsCollectedReadFailuresOnceAfterAdmissionIsReleased(bool commitFails)
    {
        using var scope = TestDatabaseScope.Create();
        ApplicationDatabaseScope databaseScope = scope.DatabaseScope;
        string root = Path.Combine(scope.DirectoryPath, "BMS");
        Directory.CreateDirectory(root);
        BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(databaseScope, scope.SongDbPath, songDb =>
        {
            songDb.CreateTable<LR2SongDB.song>();
            songDb.CreateTable<LR2SongDB.folder>();
            songDb.CreateTable<LR2SongDBExtended.maintenance>();
            songDb.CreateTable<LR2SongDBExtended.bmson_song>();
        });
        ChartFile Parse(string name)
        {
            string path = Path.Combine(root, name);
            File.WriteAllText(path, "#PLAYER 1\r\n#TITLE " + name + "\r\n#WAV01 missing.wav\r\n#00111:01\r\n");
            return BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path));
        }
        ChartFile[] missing = [Parse("b-missing-0.bms"), Parse("a-missing-1.bms")];
        ChartFile readable = Parse("m-readable.bms");
        new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath)
            .UpsertSongs([.. missing, readable]);
        foreach (ChartFile chart in missing) { File.Delete(chart.Path); }
        if (commitFails)
        {
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(databaseScope, scope.SongDbPath, db =>
            {
                db.Execute("CREATE TRIGGER reject_maintenance_insert BEFORE INSERT ON maintenance BEGIN SELECT RAISE(ABORT, 'forced maintenance commit failure'); END;");
                db.Execute("CREATE TRIGGER reject_maintenance_update BEFORE UPDATE ON maintenance BEGIN SELECT RAISE(ABORT, 'forced maintenance commit failure'); END;");
            });
        }

        var gate = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        TestBmsLibrary? library = null;
        var requests = new ConcurrentQueue<UiMessageRequest>();
        var admissionFreeAtRequest = new ConcurrentQueue<bool>();
        var ui = new GatedMessageDialogs(request =>
        {
            requests.Enqueue(request);
            bool entered = library!.OperationAdmission.TryEnter(out IDisposable? lease);
            lease?.Dispose();
            bool playlistEntered = library.Lr2Synchronization.PlaylistOperationAdmission.TryEnter(out IDisposable? playlistLease);
            playlistLease?.Dispose();
            admissionFreeAtRequest.Enqueue(entered && playlistEntered);
            return gate.Task;
        });
        library = new TestBmsLibrary(databaseScope, scope.SongDbPath, null, null, null,
            new BmsLibraryDialogService(ui, new TestUiScheduler(() => null)), new TestUiScheduler(() => null))
        {
            BmsCharts = [.. missing, readable]
        };
        Func<Task>? maintenanceWork = null;
        library.StartupBackgroundTaskScheduler = (name, _, _, work) =>
        {
            if (name == "installable_maintenance") { maintenanceWork = work; }
            return true;
        };
        try
        {
            library.ScheduleInitializationFollowUp(new LibraryInitializationFollowUp("maintenance-read-failure-post-test", true, 0L));
            Assert.IsNotNull(maintenanceWork, "起動後の保守が受理され、実の保守が捕捉される。");

            await maintenanceWork();

            Assert.AreEqual(1, requests.Count, "表示を完了しなくても保守は終端し、集めた失敗は一度だけまとめて投函される。");
            Assert.IsTrue(admissionFreeAtRequest.Single(), "表示の要求の時点で、保守の受付は解放済みである。");
            if (!commitFails)
            {
                // 保存の失敗を仕込んだ条件では、別の保守の保存も同じトリガーで失敗するため、成功の条件だけで確かめる。
                MaintenanceWorkflowResult next = await Task.Run(() => library.RescanResourceHealthCharts([readable]));
                Assert.IsFalse(next.Canceled, "表示が終わる前でも、新しく受付を取る別の実保守が完了できる。");
            }
            UiMessageRequest request = requests.Single();
            Assert.AreEqual(Resources.MessageBoxTitle_Error, request.Caption);
            Assert.AreEqual(MessageBoxImage.Hand, request.Icon);
            foreach (ChartFile chart in missing)
            {
                StringAssert.Contains(request.MessageBoxText, chart.Path);
            }
        }
        finally
        {
            gate.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
            library.RequestShutdown("installable-maintenance-read-failure-post-test");
        }
    }

    /// <summary>投函の表示先として、要求を記録して呼出し元が指定した結果の Task を返す、表示窓口の記録用実装です。</summary>
    private sealed class GatedMessageDialogs(Func<UiMessageRequest, Task<UiDialogResult>> handler) : IUiDialogService
    {
        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default) => handler(request);
        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request, CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();
        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class EmptyRootFileEnumerator : IRootFileEnumerator
    {
        public RootFileEnumerationResult EnumerateFiles(IEnumerable<string> rootDirectories, IEnumerable<RootFileEnumerationGroup> groups, bool verboseLog = false)
        {
            var result = new RootFileEnumerationResult { Success = true, IsComplete = true, BackendName = "fixture", TotalFileCount = 0 };
            foreach (RootFileEnumerationGroup group in groups) { result.InitializeGroup(group.Name); }
            return result;
        }
    }

}
