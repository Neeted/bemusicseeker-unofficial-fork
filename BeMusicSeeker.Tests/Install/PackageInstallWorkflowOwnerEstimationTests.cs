using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.BmsLibraryInitializationTestSupport;
using static BeMusicSeeker.Tests.ChartInfoMetadataTestSupport;

namespace BeMusicSeeker.Tests;

public sealed partial class PackageInstallWorkflowOwnerTests
{
    /// <summary>実候補探索後の交差Busyは停止・物理変更を行わず、受理Lをworker cleanup終端まで保持します。純推定へPは追加しません。</summary>
    [TestMethod]
    public async Task AutoInstall_ManagedBusyAfterInputPreparationKeepsAdmissionUntilActualCleanupWithoutReplay()
    {

        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        string installRoot = Path.Combine(root, "Library");
        string managed = Path.Combine(root, "Managed");
        string source = Path.Combine(managed, "Crossing");
        Directory.CreateDirectory(installRoot);
        Directory.CreateDirectory(source);
        string sourceFile = Path.Combine(source, "chart.bms");
        File.WriteAllText(sourceFile, CreateValidBmsText("Crossing"), Encoding.ASCII);
        // 欠損音源による保留分類ではなく、実auto-install候補を交差判定まで通します。
        File.WriteAllBytes(Path.Combine(source, "sound.wav"), [1]);
        string songDb = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDb, []);
        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = true,
            BMSInstallDir = installRoot,
            LR2CustomFolderOutputBaseDir = managed,
            FolderNameFormat = "%TITLE%",
            KeepInstallablePackagesPending = false
        };
        var library = new TestBmsLibrary(songDb, null, null, null, new FileDbReportRecordingDialogs(),
            new TestUiScheduler(() => null!), () => options)
        {
            BmsCharts = [],
            BmsonCharts = [],
            SearchTargets = [installRoot],
            ChartPackagesPending = new System.Collections.ObjectModel.ObservableCollection<ChartPackage>(),
            ChartPackagesInstalled = new System.Collections.ObjectModel.ObservableCollection<ChartPackage>()
        };
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activity = new ChartMutationActivityOwner();
        var playback = new NoOpChartMutationPlaybackPort();
        var owner = new PackageInstallWorkflowOwner(new FileDbReportRecordingDialogs(), library.OperationAdmission,
            activity, new BmsLibraryPackageInstallMutationPort(), playback, action => { action(); return true; });
        owner.AttachLibrary(library);
        LibraryMutationSessionReceipt? result = null;
        Exception? terminalFailure = null;
        int prepareHeld = 0;
        owner.CompletionPublished += receipt => result = receipt.SessionReceipt;
        owner.FailurePublished += failure => { result = failure.CommandResult.SessionReceipt; terminalFailure = failure.Exception; };
        owner.StatusChanged += snapshot =>
        {
            if (snapshot.IsActive && snapshot.CompletedPathCount == 1 && Interlocked.Exchange(ref prepareHeld, 1) == 0)
            {
                prepared.TrySetResult();
                releasePreparation.Task.GetAwaiter().GetResult();
            }
        };
        activity.ActivityChanged += (_, _) =>
        {
            if (!activity.IsActive)
            {
                cleanup.TrySetResult();
                releaseCleanup.Task.GetAwaiter().GetResult();
            }
        };
        IDisposable? held = null;
        Task? idle = null;
        try
        {
            Assert.IsTrue(owner.Enqueue([source]));
            idle = owner.WaitForIdleAsync();
            await Task.WhenAny(prepared.Task, idle);
            Assert.IsTrue(prepared.Task.IsCompletedSuccessfully, "実準備通知に先行する失敗終端を成功と扱いません。");
            Assert.IsTrue(library.OperationAdmission.IsActive);
            Assert.IsTrue(library.Lr2Synchronization.PlaylistOperationAdmission.TryEnter(out IDisposable probe));
            probe.Dispose();
            Assert.IsTrue(library.Lr2Synchronization.PlaylistOperationAdmission.TryEnter(out held));
            Assert.AreEqual(0, playback.StopCount);
            releasePreparation.TrySetResult();
            await Task.WhenAny(cleanup.Task, idle);
            Assert.IsTrue(cleanup.Task.IsCompletedSuccessfully, "実cleanupに先行する終端を成功と扱いません。");
            Assert.IsFalse(idle.IsCompleted);
            Assert.IsTrue(library.OperationAdmission.IsActive);
            Assert.IsFalse(owner.Enqueue([source]));
            Assert.AreEqual(0, playback.StopCount);
            Assert.IsTrue(File.Exists(sourceFile));
            Assert.IsFalse(Directory.Exists(Path.Combine(installRoot, "Crossing")));
            Assert.IsTrue(library.ChartPackagesPending.SelectMany(package => package.ChartEntries)
                .All(entry => (entry.Chart.Status & ChartFileStatus.SEARCHING) == 0));
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(songDb).OpenSongDbReadOnly())
            {
                Assert.AreEqual(1, db.Table<LR2SongDBExtended.install>().Count());
                Assert.IsNull(db.Find<LR2SongDB.song>(Path.Combine(installRoot, "Crossing", "chart.bms")));
            }
            releaseCleanup.TrySetResult();
            await idle;
            Assert.IsNotNull(result, terminalFailure?.ToString());
            Assert.IsTrue(result.HasRequiredFailure);
            StringAssert.Contains(result.ItemFailures.Single().Failure.Message, BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy);
            Assert.IsFalse(library.OperationAdmission.IsActive);
            held.Dispose(); held = null;
            Assert.IsTrue(File.Exists(sourceFile), "P解放だけでBusy対象を再実行しません。");
            Assert.AreEqual(1, library.ChartPackagesPending.Count);
            Assert.IsTrue(library.OperationAdmission.TryEnter(out IDisposable next));
            next.Dispose();
        }
        finally
        {
            releasePreparation.TrySetResult();
            releaseCleanup.TrySetResult();
            try { if (idle != null) { await idle; } await owner.WaitForIdleAsync(); }
            finally { held?.Dispose(); library.RequestShutdown("managed-busy-test"); Directory.Delete(root, recursive: true); }
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProductionImport_StopsDispatchAndApplyThenJoinsSuccessfulSiblingBeforeAdmissionRelease(bool fault)
    {

        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
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
            PendingInstallEstimateMaxParallelPackages = 2
        };
        var library = new TestBmsLibrary(songDb, null, null, null, new FileDbReportRecordingDialogs(),
            new TestUiScheduler(() => null), () => options,
            CapturedChartFileScanner.FromFixture([installedChart],
                new Dictionary<string, IEnumerable<string>> { [installed] = ["sound.wav"] }, [installed]),
            observer, admission)
        {
            SearchTargets = [Path.Combine(root, "Library")],
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
            using (LR2SongDBExtended connection = new BmsLibraryDbGateway(songDb).OpenSongDbReadOnly())
            {
                Assert.AreEqual(4, connection.Table<LR2SongDBExtended.install>().Count(), "保留登録の実確定後も同じ操作が推定を所有します。");
            }
            Assert.IsTrue(admission.IsActive);
            new BmsLibraryDbGateway(songDb).UpsertChartInfos([CreateChartInfoRow(newSha, newMd5, BmsLibraryDbGateway.CurrentChartInfoParserVersion)]);
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
                    finally { library.RequestShutdown("accepted-estimation-test"); Directory.Delete(root, recursive: true); }
                }
            }
        }
    }

    [TestMethod]
    public async Task ProductionHydration_BlocksNewImportUntilActualInputPublicationCompletes()
    {

        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "Library"));
        string source = Path.Combine(root, "input");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "diff.bms"), CreateValidBmsText("Song"), Encoding.ASCII);
        string songDb = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDb, []);
        var admission = new ChartFileOperationSynchronizer();
        var background = new ConcurrentQueue<Func<Task>>();
        var options = new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false, ScanBmsFilesOnStartup = true, KeepInstallablePackagesPending = true };
        var observer = new HeldSuccessfulEvaluations(false);
        var library = new TestBmsLibrary(songDb, null, null, null, new FileDbReportRecordingDialogs(),
            new TestUiScheduler(() => null), () => options,
            CapturedChartFileScanner.FromFixture([], new Dictionary<string, IEnumerable<string>>(), []), observer, admission)
        {
            SearchTargets = [Path.Combine(root, "Library")],
            StartupBackgroundTaskScheduler = (_, _, _, work) => { background.Enqueue(work); return true; }
        };
        var owner = new PackageInstallWorkflowOwner(new FileDbReportRecordingDialogs(), admission,
            new ChartMutationActivityOwner(), new BmsLibraryPackageInstallMutationPort(),
            new NoOpChartMutationPlaybackPort(), action => { action(); return true; });
        owner.AttachLibrary(library);
        var publishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? hydration = null;
        void HoldPublication(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(BMSLibrary.ChartInfoIndexVersion))
            {
                publishing.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
        }
        try
        {
            library.Initialize(null, null, BMSLibrary.LibraryInitializeMode.Startup);
            while (background.TryDequeue(out Func<Task>? work)) { await work(); }
            string sha = new('9', 64);
            string md5 = new('8', 32);
            new BmsLibraryDbGateway(songDb).UpsertChartInfos([CreateChartInfoRow(sha, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion)]);
            library.PropertyChanged += HoldPublication;
            InvokeDeferredChartInfoHydration(library, "prior_input_update", queueFullBackfillAfterHydration: false);
            Assert.IsTrue(background.TryDequeue(out Func<Task>? hydrationWork));
            hydration = Task.Run(hydrationWork);
            await Task.WhenAny(publishing.Task, hydration);
            if (!publishing.Task.IsCompleted) { await hydration; Assert.Fail("実hydrationが入力公開前に終端しました。"); }
            Assert.IsFalse(hydration.IsCompleted);
            Assert.IsTrue(admission.IsActive);
            Assert.IsFalse(owner.Enqueue([source]), "実背景入力更新が公開終端へ到達するまで新しい取り込み・推定を開始しません。");
            Assert.IsFalse(observer.FirstApplied.Task.IsCompleted);
            Assert.AreEqual(0, library.ChartPackagesPending.Count);
            release.TrySetResult();
            await hydration;
            Assert.AreEqual(sha, library.ResolveChartInfo(sha, md5).sha256);
            Assert.IsFalse(admission.IsActive);
            Assert.IsTrue(owner.Enqueue([source]));
            await owner.WaitForIdleAsync();
            Assert.AreEqual(1, library.ChartPackagesPending.Count);
        }
        finally
        {
            release.TrySetResult();
            try { if (hydration != null) { await hydration; } }
            finally
            {
                library.PropertyChanged -= HoldPublication;
                try { await owner.WaitForIdleAsync(); }
                finally
                {
                    try { while (background.TryDequeue(out Func<Task>? work)) { await work(); } }
                    finally { library.RequestShutdown("prior-hydration-test"); Directory.Delete(root, recursive: true); }
                }
            }
        }
    }

    private sealed class HeldSuccessfulEvaluations(bool fault) : IInstallEstimationExecutionObserver
    {
        internal readonly TaskCompletionSource FirstApplied = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource SecondEvaluated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ThirdEvaluated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseSecond = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseThird = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly IOException ExpectedFailure = new("実評価の終端失敗");
        internal int FourthStarted;

        public IDisposable BeginWorkItem(InstallEstimationWorkItemObservation observation)
        {
            if (observation.OrderIndex == 3) { Interlocked.Increment(ref FourthStarted); }
            return new EvaluationScope();
        }

        public void ObserveEvaluationCompleted(InstallEstimationAppliedObservation observation)
        {
            if (observation.OrderIndex == 1)
            {
                SecondEvaluated.TrySetResult();
                ReleaseSecond.Task.GetAwaiter().GetResult();
                if (fault) { throw ExpectedFailure; }
            }
            if (observation.OrderIndex == 2)
            {
                ThirdEvaluated.TrySetResult();
                ReleaseThird.Task.GetAwaiter().GetResult();
            }
        }

        public void ObserveResultApplied(InstallEstimationAppliedObservation observation)
        {
            if (observation.OrderIndex == 0) { FirstApplied.TrySetResult(); }
        }

        public void ObserveDispatchStopped() => Stopped.TrySetResult();

        public void ObserveProgress(InstallEstimationProgressObservation observation) { }

        private sealed class EvaluationScope : IDisposable { public void Dispose() { } }
    }
}
