using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.Lr2SongDbSyncTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class InstallableMaintenanceAdmissionTests
{
    [TestMethod]
    public async Task QueuedLr2AndAcceptedMaintenance_WaitForOneAdmissionAndPublishBothActualUpdates()
    {

        using var scope = TestDatabaseScope.Create();
        string root = Path.Combine(scope.DirectoryPath, "BMS");
        Directory.CreateDirectory(root);
        string chart = Path.Combine(root, "chart.bms");
        WriteBasicBms(chart, "Song");
        File.WriteAllBytes(Path.Combine(root, "sound.wav"), [1]);
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chart);
        using (var db = new LR2SongDBExtended(scope.SongDbPath))
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
        var library = new TestBmsLibrary(scope.SongDbPath, null, null, null, () => options,
            TestBmsFactory.MissingEverythingBridge,
            CapturedChartFileScanner.FromFixture([chart], new Dictionary<string, IEnumerable<string>>
            {
                [root] = ["sound.wav"]
            }, [root]), new TestUiScheduler(() => null), new EmptyRootFileEnumerator())
        { SearchTargets = [root] };
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
            using (LR2SongDBExtended before = new BmsLibraryDbGateway(scope.SongDbPath).OpenSongDbReadOnly())
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
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.SongDbPath).OpenSongDbReadOnly())
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
