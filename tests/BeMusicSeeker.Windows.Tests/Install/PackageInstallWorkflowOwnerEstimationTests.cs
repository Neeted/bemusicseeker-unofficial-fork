using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryInitializationTestSupport;
using static BeMusicSeeker.TestSupport.Components.ChartInfoComponentTestSupport;
using static BeMusicSeeker.TestSupport.Components.ChartInfoHydrationTestSupport;
using static BeMusicSeeker.TestSupport.Components.PackageInstallWorkflowOwnerTestSupport;

namespace BeMusicSeeker.Tests;

public sealed partial class PackageInstallWorkflowOwnerTests
{
    /// <summary>実管理ZIPを含む保留確定から直接推定へ進み、取消・例外後も既適用結果と未適用管理入力を保持する。開始済み兄弟のjoin中は受付を保持し、受理済み必須更新の完了も確認します。</summary>
    /// <remarks>実ライブラリのDB確定・譜面情報公開と共通受付を接続する。開始した背景Taskとownerのidle終端を回収し、通知の到達だけで受付解放としない。</remarks>
    // falseは利用者取消、trueは二件目の実評価例外。どちらも既適用Aを残し、成功終端Cを新たに適用しない。
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProductionImport_StopsDispatchAndApplyThenJoinsSuccessfulSiblingBeforeAdmissionRelease(bool fault)
    {
        var databaseScope = new ApplicationDatabaseScope();

        using var caseWorkspace1 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(caseWorkspace1.RootDirectory, nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        string installed = Path.Combine(root, "Library", "Song");
        Directory.CreateDirectory(installed);
        string installedChart = Path.Combine(installed, "chart.bms");
        File.WriteAllText(installedChart, CreateValidBmsText("Song"), Encoding.ASCII);
        File.WriteAllBytes(Path.Combine(installed, "sound.wav"), [1]);
        string[] sources = Enumerable.Range(0, 5).Select(index => Path.Combine(root, "Input" + index)).ToArray();
        foreach (string source in sources)
        {
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "diff.bms"), CreateValidBmsText("Song") + "#PLAYLEVEL " + (10 + Array.IndexOf(sources, source)) + "\r\n", Encoding.ASCII);
        }
        string managedArchiveRoot = TempDirectoryPublisher.Get();
        string managedArchive = Path.Combine(managedArchiveRoot, "Input3.zip");
        ZipFile.CreateFromDirectory(sources[3], managedArchive);
        sources[3] = managedArchive;
        var extractedRoots = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new ArchiveInputFileMutationService(path =>
        {
            string? extractedRoot = Path.GetDirectoryName(path);
            if (extractedRoot != null && TempDirectoryPublisher.IsManagedPath(extractedRoot)) { extractedRoots.Add(extractedRoot); }
        });
        string songDb = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDb, []);
        var background = new ConcurrentQueue<Func<Task>>();
        var admission = new ChartFileOperationSynchronizer();
        var observer = new HeldSuccessfulEvaluations(fault);
        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = false,
            ScanBmsFilesOnStartup = true,
            KeepInstallablePackagesPending = true,
            AutoInstallAfterAutomaticEstimation = true,
            PendingInstallEstimateMaxParallelPackages = 2
        };
        var library = new TestBmsLibrary(databaseScope, songDb, null, null, files, new FileDbReportRecordingDialogs(),
            new TestUiScheduler(() => null), () => options,
            CapturedChartFileScanner.FromFixture([installedChart],
                new Dictionary<string, IEnumerable<string>> { [installed] = ["sound.wav"] }, [installed]),
            observer, admission, settings: TestBmsFactory.CreateRootSettings([Path.Combine(root, "Library")]))
        {

            StartupBackgroundTaskScheduler = (_, _, _, work) => { background.Enqueue(work); return true; }
        };
        var owner = new PackageInstallWorkflowOwner(new FileDbReportRecordingDialogs(), admission,
            new ChartMutationActivityOwner(), new BmsLibraryPackageInstallMutationPort(),
            new NoOpChartMutationPlaybackPort(), action => { action(); return true; });
        PackageInstallFailure? failure = null;
        owner.FailurePublished += value => failure = value;
        owner.AttachLibrary(library);
        Task? acceptedHydration = null;
        string newSha = new('9', 64);
        string newMd5 = new('8', 32);
        try
        {
            library.Initialize(null, null, BMSLibrary.LibraryInitializeMode.Startup);
            while (background.TryDequeue(out Func<Task>? work)) { await work(); }
            Assert.IsTrue(owner.Enqueue(sources.Take(4)));
            var arrivals = Task.WhenAll(observer.FirstApplied.Task, observer.SecondEvaluated.Task, observer.ThirdEvaluated.Task);
            Task initialIdle = owner.WaitForIdleAsync();
            await Task.WhenAny(arrivals, observer.Stopped.Task, initialIdle);
            Assert.IsTrue(arrivals.IsCompletedSuccessfully, "実停止・終端が評価到達通知に先行したため、finallyで兄弟を解放して全joinします。");
            using (LR2SongDBExtended connection = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDb).OpenSongDbReadOnly())
            {
                Assert.AreEqual(4, connection.Table<LR2SongDBExtended.install>().Count(), "保留登録の実確定後も同じ操作が推定を所有します。");
            }
            Assert.IsTrue(admission.IsActive);
            new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDb).UpsertChartInfos([CreateChartInfoRow(newSha, newMd5, BmsLibraryDbGateway.CurrentChartInfoParserVersion)]);
            InvokeDeferredChartInfoHydration(library, "accepted_during_estimation", queueFullBackfillAfterHydration: false);
            Assert.IsTrue(background.TryDequeue(out Func<Task>? hydrationWork));
            acceptedHydration = hydrationWork();
            Assert.IsFalse(acceptedHydration.IsCompleted, "受理済みの実読込みは推定終端まで入力を書き換えません。");
            Assert.IsNull(library.ResolveChartInfo(newSha, newMd5));
            Assert.IsFalse(owner.Enqueue([sources[4]]), "実評価中の追加要求は予約しません。");
            if (!fault) { owner.CancelAll(); }
            observer.ReleaseSecond.TrySetResult();
            await Task.WhenAny(observer.Stopped.Task, owner.WaitForIdleAsync());
            Assert.IsTrue(observer.Stopped.Task.IsCompletedSuccessfully, "dispatch停止のtyped観測より前に終端しました。");
            Task idle = owner.WaitForIdleAsync();
            Assert.IsFalse(idle.IsCompleted, "取消・例外観測後も開始済み成功評価Cの終端を待ちます。");
            Assert.IsTrue(admission.IsActive);
            Assert.AreEqual(0, observer.FourthStarted);
            observer.ReleaseThird.TrySetResult();
            await idle;
            await acceptedHydration;
            Assert.AreEqual(newSha, library.ResolveChartInfo(newSha, newMd5).sha256, "受理済み必須更新をBusyで捨てず実入力へ適用します。");
            Assert.IsNotNull(failure);
            Assert.IsTrue(failure.CommandResult.SessionReceipt.DurableCommit);
            if (fault) { Assert.AreSame(observer.ExpectedFailure, failure.Exception); }
            else { Assert.IsInstanceOfType<OperationCanceledException>(failure.Exception); }
            Assert.IsFalse(admission.IsActive);
            Assert.AreEqual(0, observer.FourthStarted);
            ChartPackage first = library.ChartPackagesPending.Single(package => package.path == sources[0]);
            Assert.AreEqual(installed, first.ChartEntries.Single().Chart.InstallDestination);
            foreach (ChartPackage package in library.ChartPackagesPending.Where(package => package.path != sources[0]))
            {
                Assert.IsTrue(package.ChartEntries.All(entry => string.IsNullOrEmpty(entry.Chart.InstallDestination)), "停止後に正常終了したCも次の明示推定へ残します。");
            }
            Assert.IsTrue(library.ChartPackagesPending.SelectMany(package => package.ChartEntries).All(entry => (entry.Chart.Status & ChartFileStatus.SEARCHING) == 0));
            Assert.AreEqual(4, library.ChartPackagesPending.Count);
            Assert.IsFalse(File.Exists(managedArchive));
            Assert.AreEqual(1, extractedRoots.Count);
            ChartPackage managedPending = library.ChartPackagesPending.Single(package => extractedRoots.Contains(package.path));
            Assert.IsTrue(managedPending.ChartEntries.All(entry => File.Exists(entry.Chart.Path)));
            Assert.IsTrue(string.IsNullOrEmpty(managedPending.ChartEntries.Single().Chart.InstallDestination), "未開始の管理入力は次の明示推定へ保全します。");
            Assert.IsNull(failure.CommandResult.EstimatedInstallReceipt);
            Assert.AreEqual(0, library.ChartPackagesInstalled.Count);
            Assert.IsTrue(sources.Take(3).All(path => Directory.Exists(path)));
            options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = false,
                ScanBmsFilesOnStartup = true,
                KeepInstallablePackagesPending = true,
                PendingInstallEstimateMaxParallelPackages = 2
            };
            failure = null;
            Assert.IsTrue(owner.Enqueue([sources[4]]));
            await owner.WaitForIdleAsync();
            Assert.IsNull(failure);
            Assert.AreEqual(5, library.ChartPackagesPending.Count);
            Assert.AreEqual(installed, library.ChartPackagesPending.Single(package => package.path == sources[4]).ChartEntries.Single().Chart.InstallDestination);
        }
        finally
        {
            observer.ReleaseSecond.TrySetResult();
            observer.ReleaseThird.TrySetResult();
            try { await owner.WaitForIdleAsync(); }
            finally
            {
                try { if (acceptedHydration != null) { await acceptedHydration; } }
                finally
                {
                    try { while (background.TryDequeue(out Func<Task>? work)) { await work(); } }
                    finally
                    {
                        library.RequestShutdown("accepted-estimation-test");
                        foreach (string path in extractedRoots) { TempDirectoryPublisher.TryDeleteManagedPath(path); }
                        TempDirectoryPublisher.TryDeleteManagedPath(managedArchiveRoot);
                        Directory.Delete(root, recursive: true);
                    }
                }
            }
        }
    }

}
