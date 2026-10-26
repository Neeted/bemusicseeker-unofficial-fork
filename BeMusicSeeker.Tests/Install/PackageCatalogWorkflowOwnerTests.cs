using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PackageCatalogWorkflowOwnerTests
{
    /// <summary>library未接続の全解除は変更と活動を始めず正常終了し、未接続窓口への誤転送を検出します。</summary>
    [TestMethod]
    public async Task ClearAllAsync_WithoutAttachedLibraryIsNoOp()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        PackageCatalogWorkflowOwner owner = CreateOwner(() => null!, events, store, AcceptedDialogs());

        PackageCatalogMutationResult result = await owner.ClearAllAsync(PackageCatalogSection.Pending);

        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(result.ShouldApplyView);
        Assert.AreEqual(0, store.RemoveAllCount);
        CollectionAssert.AreEqual(Array.Empty<string>(), events);
    }

    /// <summary>保留・導入済み両区画の全解除で確認を要求し、指定区画の変更を一つの活動・抑制内で実行して、区画誤転送や解放順序の欠落を検出します。</summary>
    [DataTestMethod]
    [DataRow((int)PackageCatalogSection.Pending)]
    [DataRow((int)PackageCatalogSection.Installed)]
    public async Task ClearAllAsync_PreservesConfirmationAndMutationBoundaryOrder(int sectionValue)
    {

        var section = (PackageCatalogSection)sectionValue;
        var events = new List<string>();
        var store = new RecordingStore(events);
        FakeUiDialogService dialogs = AcceptedDialogs();
        PackageCatalogWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, dialogs);

        PackageCatalogMutationResult result = await owner.ClearAllAsync(section);

        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(result.ShouldApplyView);
        Assert.AreEqual(section, store.LastSection);
        Assert.IsNotNull(dialogs.ConfirmationRequest);
        CollectionAssert.AreEqual(
            new[] { "activity-start", "suppression-start", "store-remove-all", "suppression-end", "activity-end" },
            events);
    }

    /// <summary>全解除確認の取消は変更もview反映も要求せず、未承認操作の副作用を検出します。</summary>
    [TestMethod]
    public async Task ClearAllAsync_RejectionSkipsMutationAndViewApply()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var dialogs = new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
        };
        PackageCatalogWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, dialogs);

        PackageCatalogMutationResult result = await owner.ClearAllAsync(PackageCatalogSection.Installed);

        Assert.IsFalse(result.Succeeded);
        Assert.IsNull(result.Failure);
        Assert.IsFalse(result.ShouldApplyView);
        Assert.AreEqual(0, store.RemoveAllCount);
        CollectionAssert.AreEqual(Array.Empty<string>(), events);
    }

    /// <summary>全解除の表示失敗は元原因を保持して変更・view反映を止め、確認失敗を成功へ読み替える誤りを検出します。</summary>
    [TestMethod]
    public async Task ClearAllAsync_ConfirmationFailureSkipsMutationAndViewApply()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var failure = new InvalidOperationException("dialog failed");
        var dialogs = new FakeUiDialogService { ConfirmationResult = UiDialogResult.Failed(failure) };
        PackageCatalogWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, dialogs);

        PackageCatalogMutationResult result = await owner.ClearAllAsync(PackageCatalogSection.Pending);

        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(result.Failure);
        Assert.AreSame(failure, result.Failure.InnerException);
        Assert.IsFalse(result.ShouldApplyView);
        Assert.AreEqual(0, store.RemoveAllCount);
        CollectionAssert.AreEqual(Array.Empty<string>(), events);
    }

    /// <summary>保留packageの表示titleを確認要求へ含め、同じ対象一件を活動・抑制内で変更して、確認要求の欠落や対象・解放順序の取り違えを検出します。</summary>
    [TestMethod]
    public async Task RemovePackageAsync_PendingConfirmationPrecedesMutation()
    {

        var events = new List<string>();
        var store = new RecordingStore(events);
        FakeUiDialogService dialogs = AcceptedDialogs();
        PackageCatalogWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, dialogs);
        var package = new ChartPackage { path = @"C:\Pending\package" };

        PackageCatalogMutationResult result = await owner.RemovePackageAsync(
            PackageCatalogSection.Pending,
            package);

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(result.Failure);
        Assert.IsTrue(result.ShouldApplyView);
        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "suppression-start",
                "store-remove-packages",
                "suppression-end",
                "activity-end"
            },
            events);
        Assert.AreSame(package, store.LastPackages[0]);
        StringAssert.Contains(dialogs.ConfirmationRequest!.MessageBoxText, package.DisplayTitle);
    }

    /// <summary>package解除後に空となる区画を反映結果へ渡し、画面の区画解除漏れを検出します。</summary>
    [TestMethod]
    public async Task RemovePackageAsync_PublishesEmptySectionIntent()
    {
        var events = new List<string>();
        var store = new RecordingStore(events)
        {
            EmptySection = PackageCatalogSection.Pending
        };
        PackageCatalogWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, AcceptedDialogs());

        PackageCatalogMutationResult result = await owner.RemovePackageAsync(
            PackageCatalogSection.Pending,
            new ChartPackage());

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(PackageCatalogSection.Pending, result.EmptySection);
    }

    /// <summary>導入済みpackage解除は確認を出さずstoreへ渡し、不要な確認で処理を止める誤りを検出します。</summary>
    [TestMethod]
    public async Task RemovePackageAsync_InstalledDoesNotPrompt()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var dialogs = new FakeUiDialogService();
        PackageCatalogWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, dialogs);

        var package = new ChartPackage();
        PackageCatalogMutationResult result = await owner.RemovePackageAsync(
            PackageCatalogSection.Installed,
            package);

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(result.Failure);
        Assert.IsTrue(result.ShouldApplyView);
        Assert.IsNull(dialogs.ConfirmationRequest);
        CollectionAssert.Contains(events, "store-remove-packages");
    }

    /// <summary>保留package解除の取消は変更・view反映を行わず、取消後の実行を検出します。</summary>
    [TestMethod]
    public async Task RemovePackageAsync_PendingRejectionSkipsMutationAndViewApply()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var dialogs = new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
        };
        PackageCatalogWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, dialogs);

        PackageCatalogMutationResult result = await owner.RemovePackageAsync(
            PackageCatalogSection.Pending,
            new ChartPackage());

        Assert.IsFalse(result.Succeeded);
        Assert.IsNull(result.Failure);
        Assert.IsFalse(result.ShouldApplyView);
        Assert.AreEqual(0, store.RemovePackagesCount);
        CollectionAssert.AreEqual(Array.Empty<string>(), events);
    }

    /// <summary>保留package解除の表示失敗は元原因を返して変更を止め、表示できない確認の代替実行を検出します。</summary>
    [TestMethod]
    public async Task RemovePackageAsync_PendingConfirmationFailureSkipsMutationAndViewApply()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var failure = new InvalidOperationException("dialog failed");
        var dialogs = new FakeUiDialogService { ConfirmationResult = UiDialogResult.Failed(failure) };
        PackageCatalogWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, dialogs);

        PackageCatalogMutationResult result = await owner.RemovePackageAsync(
            PackageCatalogSection.Pending,
            new ChartPackage());

        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(result.Failure);
        Assert.AreSame(failure, result.Failure.InnerException);
        Assert.IsFalse(result.ShouldApplyView);
        Assert.AreEqual(0, store.RemovePackagesCount);
        CollectionAssert.AreEqual(Array.Empty<string>(), events);
    }

    /// <summary>保留選択からpackage解決と解除を同じ抑制範囲で行い、対象接続漏れと別々の変更確定を検出します。</summary>
    [TestMethod]
    public async Task RemoveSelectionAsync_ResolvesAndRemovesInsideOneMutation()
    {
        var events = new List<string>();
        var expectedPackage = new ChartPackage();
        var store = new RecordingStore(events) { ResolvedPackages = [expectedPackage] };
        PackageCatalogWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, AcceptedDialogs());
        ChartOperationTarget target = CreateTarget();

        var request = PackageCatalogRemovalRequest.CreatePending([target]);
        PackageCatalogMutationResult result = await owner.RemoveSelectionAsync(
            request);

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(result.Failure);
        Assert.IsTrue(result.ShouldApplyView);
        Assert.AreEqual(PackageCatalogSection.Pending, store.LastSection);
        CollectionAssert.AreEqual(new[] { target }, (System.Collections.ICollection)store.LastTargets);
        CollectionAssert.AreEqual(new[] { expectedPackage }, (System.Collections.ICollection)store.LastPackages);
        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "suppression-start",
                "store-resolve",
                "store-remove-packages",
                "suppression-end",
                "activity-end"
            },
            events);
    }

    /// <summary>保留選択解除の確認失敗は変更・view反映を止め、別入口だけで失敗を無視する誤りを検出します。</summary>
    [TestMethod]
    public async Task RemoveSelectionAsync_DialogFailureSkipsMutationAndViewApply()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var failure = new InvalidOperationException("dialog failed");
        var dialogs = new FakeUiDialogService { ConfirmationResult = UiDialogResult.Failed(failure) };
        PackageCatalogWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, dialogs);

        PackageCatalogMutationResult result = await owner.RemoveSelectionAsync(
            PackageCatalogRemovalRequest.CreatePending([CreateTarget()]));

        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(result.Failure);
        Assert.AreSame(failure, result.Failure.InnerException);
        Assert.IsFalse(result.ShouldApplyView);
        Assert.AreEqual(0, store.RemovePackagesCount);
        CollectionAssert.AreEqual(Array.Empty<string>(), events);
    }

    /// <summary>package解除の元失敗と空区画の反映を保持し、活動終了の表示購読者の失敗を変更結果へ追加せず他の後片付けを終えます。</summary>
    [TestMethod]
    public async Task RemovePackageAsync_PreservesMutationDespiteActivityNotificationFailure()
    {
        var events = new List<string>();
        var mutationFailure = new InvalidOperationException("remove failed");
        var activityFailure = new InvalidOperationException("activity notification failed");
        var store = new RecordingStore(events)
        {
            Failure = mutationFailure,
            EmptySection = PackageCatalogSection.Installed
        };
        var phaseObserver = new RecordingPhaseObserver(events) { EndActivityFailure = activityFailure };
        PackageCatalogWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, AcceptedDialogs(), phaseObserver);

        PackageCatalogMutationResult result = await owner.RemovePackageAsync(
            PackageCatalogSection.Installed,
            new ChartPackage());

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.ShouldApplyView);
        Assert.AreEqual(PackageCatalogSection.Installed, result.EmptySection);
        Assert.AreSame(mutationFailure, result.Failure);
        CollectionAssert.AreEqual(
            new[] { "activity-start", "suppression-start", "store-remove-packages", "suppression-end", "activity-end" },
            events);
    }

    /// <summary>package解除後の空区画照会も失敗した場合は両原因を変更失敗から順に保持し、二次照会が主因を隠す誤りを検出します。</summary>
    [TestMethod]
    public async Task RemovePackageAsync_PreservesMutationBeforeEmptySectionFailures()
    {
        var events = new List<string>();
        var mutationFailure = new InvalidOperationException("remove failed");
        var emptySectionFailure = new InvalidOperationException("empty-section query failed");
        var store = new RecordingStore(events)
        {
            Failure = mutationFailure,
            EmptySectionFailure = emptySectionFailure
        };
        PackageCatalogWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, AcceptedDialogs());

        PackageCatalogMutationResult result = await owner.RemovePackageAsync(
            PackageCatalogSection.Installed,
            new ChartPackage());

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.ShouldApplyView);
        Assert.IsInstanceOfType<AggregateException>(result.Failure);
        var exception = (AggregateException)result.Failure;
        Assert.AreSame(mutationFailure, exception.InnerExceptions[0]);
        Assert.AreSame(emptySectionFailure, exception.InnerExceptions[1]);
    }

    /// <summary>共通受付のBusy中は確認・変更・活動を始めず、解放後の明示要求だけを処理する受付漏れと残留を検出します。</summary>
    [TestMethod]
    public async Task ClearAllAsync_FailsFastWhenSharedChartFileGateIsBusyThenRunsAfterRelease()
    {
        var synchronizer = new ChartFileOperationSynchronizer();
        var events = new List<string>();
        var store = new RecordingStore(events);
        var phaseObserver = new RecordingPhaseObserver(events);
        ChartMutationActivityOwner activity = new();
        FakeUiDialogService dialogs = AcceptedDialogs();
        var owner = new PackageCatalogWorkflowOwner(
            CreateLibrary,
            synchronizer,
            activity,
            dialogs,
            new NoOpChartMutationPlaybackPort(),
            mutation => Task.Factory.StartNew(
                mutation,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap(),
            store);
        owner.MutationPhasePublished += phaseObserver.OnPhasePublished;
        activity.ActivityChanged += phaseObserver.OnActivityChanged;
        Assert.IsTrue(synchronizer.TryEnter(out IDisposable incumbent));
        try
        {
            PackageCatalogMutationResult removal = await owner.ClearAllAsync(PackageCatalogSection.Installed);
            Assert.IsFalse(removal.Succeeded);
            Assert.IsFalse(removal.ShouldApplyView);
            Assert.AreEqual(0, store.RemoveAllCount);
            Assert.IsNull(dialogs.ConfirmationRequest);
            CollectionAssert.AreEqual(Array.Empty<string>(), events);
        }
        finally
        {
            incumbent.Dispose();
        }

        PackageCatalogMutationResult result = await owner.ClearAllAsync(PackageCatalogSection.Installed);
        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, store.RemoveAllCount);
    }

    /// <summary>管理入力の単独・選択・全解除は停止の実終端を待ち、同じLで競合要求を拒否します。停止失敗・終了取消では入力bytesと主失敗を保ち、次の明示要求だけを受け付けます。</summary>
    /// <remarks>実owner→本番store→実SQLite/FSを動かし、停止portだけを制御します。管理外・導入済み・無関係な管理入力への削除拡大も検出します。</remarks>
    [DataTestMethod]
    // 同一test processの独立compositionが共通session markerを初期公開するfixture都合で非並列とします。本番の同一Lによる直列化と、下記のL競合判定は維持します。
    [DoNotParallelize]
    [DataRow("package", "success")]
    [DataRow("selection", "success")]
    [DataRow("all", "success")]
    [DataRow("package", "failure")]
    [DataRow("all", "cancel")]
    public async Task PendingRemoval_WaitsForStopAndRetainsInputOnFailure(string route, string outcome)
    {
        string root = Path.Combine(Path.GetTempPath(), "bms-catalog-removal-" + Guid.NewGuid().ToString("N"));
        string managed = TempDirectoryPublisher.Get("catalog-removal");
        string unrelated = TempDirectoryPublisher.Get("catalog-unrelated");
        Directory.CreateDirectory(root);
        string chartPath = Path.Combine(managed, "chart.bms");
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("#TITLE pending\n");
        File.WriteAllBytes(chartPath, bytes);
        string borrowed = Path.Combine(root, "borrowed.bms");
        string installed = Path.Combine(root, "installed.bms");
        File.WriteAllBytes(borrowed, bytes);
        File.WriteAllBytes(installed, bytes);
        File.WriteAllBytes(Path.Combine(unrelated, "keep.bms"), bytes);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playback = new ControlledPlayback(() => { entered.TrySetResult(); return stop.Task; });
        Task<PackageCatalogMutationResult>? removal = null;
        TestBmsLibrary? library = null;
        try
        {
            string db = Path.Combine(root, "song.db");
            var gateway = new BmsLibraryDbGateway(db);
            gateway.EnsureAppOwnedSchema();
            gateway.EnsureLibraryStartupSchema();
            library = new TestBmsLibrary(db, null, null, new ResilientFileMutationService(),
                new FileDbReportRecordingDialogs(), new TestUiScheduler(() => null));
            var pending = ChartPackage.FromChartEntries([PackageChartEntry.FromPath(chartPath)]);
            pending.path = managed;
            var borrowedPackage = new ChartPackage { path = borrowed };
            library.ChartPackagesPending = new ObservableCollection<ChartPackage>([pending, borrowedPackage]);
            library.ChartPackagesInstalled = new ObservableCollection<ChartPackage>([new ChartPackage { path = installed }]);
            gateway.ApplyInstallTableMutation([], [pending, borrowedPackage]);
            var activity = new ChartMutationActivityOwner();
            var owner = new PackageCatalogWorkflowOwner(() => library, library.OperationAdmission, activity,
                AcceptedDialogs(), playback, mutation => Task.Run(mutation));
            bool suppressed = false;
            owner.MutationPhasePublished += (_, e) => suppressed = e.Phase == PackageCatalogMutationPhase.RefreshSuppressionStarted;
            var target = new ChartOperationTarget(pending.ChartEntries[0].Chart, null,
                ChartOperationSourceScope.PendingPackage, false, true, false, ChartOperationCapabilities.UpdateInstallDestination,
                packageEntry: pending.ChartEntries[0]);
            removal = route switch
            {
                "package" => owner.RemovePackageAsync(PackageCatalogSection.Pending, pending),
                "selection" => owner.RemoveSelectionAsync(PackageCatalogRemovalRequest.CreatePending([target])),
                _ => owner.ClearAllAsync(PackageCatalogSection.Pending)
            };
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, removal, "catalog-managed-stop");
            Assert.IsFalse(removal.IsCompleted);
            Assert.IsTrue(library.OperationAdmission.IsActive);
            Assert.IsTrue(activity.IsActive);
            Assert.IsTrue(suppressed);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(chartPath));
            Assert.IsFalse((await owner.ClearAllAsync(PackageCatalogSection.Pending)).Succeeded, "競合要求を予約しません。");
            Exception? expected = outcome switch
            {
                "failure" => new IOException("stop failed"),
                "cancel" => new OperationCanceledException("shutdown"),
                _ => null
            };
            if (expected == null) { stop.SetResult(); }
            else { stop.SetException(expected); }
            PackageCatalogMutationResult result = await removal;
            Assert.AreEqual(expected == null, result.Succeeded);
            Assert.AreSame(expected, result.Failure);
            if (expected != null) { CollectionAssert.AreEqual(bytes, File.ReadAllBytes(chartPath)); }
            else
            {
                Assert.IsFalse(Directory.Exists(managed));
                Assert.AreEqual(route == "all" ? 0 : 1, library.ChartPackagesPending.Count);
                using LR2SongDBExtended verify = gateway.OpenSongDbReadOnly();
                Assert.AreEqual(route == "all" ? 0 : 1, verify.ExecuteScalar<int>("SELECT COUNT(1) FROM install;"));
            }
            Assert.AreEqual(1, library.ChartPackagesInstalled.Count);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(borrowed));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(installed));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(unrelated, "keep.bms")));
            Assert.IsFalse(library.OperationAdmission.IsActive);
            Assert.IsFalse(activity.IsActive);
            Assert.IsFalse(suppressed);
            playback.Stop = () => Task.CompletedTask;
            Assert.IsTrue((await owner.ClearAllAsync(PackageCatalogSection.Pending)).Succeeded);
            Assert.AreEqual(0, library.ChartPackagesPending.Count);
            Assert.IsFalse(Directory.Exists(managed));
        }
        finally
        {
            stop.TrySetResult();
            if (removal != null) { await removal; }
            library?.RequestShutdown("catalog-removal-test");
            TempDirectoryPublisher.TryDeleteManagedPath(managed);
            TempDirectoryPublisher.TryDeleteManagedPath(unrelated);
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>借用保留だけの全解除と導入済み記録の解除は、再生停止へ入らず実体を保持します。</summary>
    [DataTestMethod]
    [DataRow((int)PackageCatalogSection.Pending)]
    [DataRow((int)PackageCatalogSection.Installed)]
    public async Task RecordOnlyRemoval_DoesNotStopPlayback(int sectionValue)
    {
        string root = Path.Combine(Path.GetTempPath(), "bms-catalog-record-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        TestBmsLibrary? library = null;
        try
        {
            string db = Path.Combine(root, "song.db");
            var gateway = new BmsLibraryDbGateway(db);
            gateway.EnsureAppOwnedSchema();
            gateway.EnsureLibraryStartupSchema();
            library = new TestBmsLibrary(db, null, null, new ResilientFileMutationService(),
                new FileDbReportRecordingDialogs(), new TestUiScheduler(() => null));
            string source = Path.Combine(root, "source.bms");
            File.WriteAllText(source, "#TITLE borrowed");
            var package = new ChartPackage { path = source };
            var section = (PackageCatalogSection)sectionValue;
            library.ChartPackagesPending = new ObservableCollection<ChartPackage>(section == PackageCatalogSection.Pending ? [package] : []);
            library.ChartPackagesInstalled = new ObservableCollection<ChartPackage>(section == PackageCatalogSection.Installed ? [package] : []);
            var playback = new ControlledPlayback(() => throw new AssertFailedException("記録解除は再生を止めません。"));
            var owner = new PackageCatalogWorkflowOwner(() => library, library.OperationAdmission, new ChartMutationActivityOwner(),
                AcceptedDialogs(), playback, mutation => Task.Run(mutation));
            Assert.IsTrue((await owner.ClearAllAsync(section)).Succeeded);
            Assert.IsTrue(File.Exists(source));
            Assert.IsFalse(library.OperationAdmission.IsActive);
        }
        finally { library?.RequestShutdown("catalog-record-test"); Directory.Delete(root, recursive: true); }
    }

    private sealed class ControlledPlayback(Func<Task> stop) : IChartMutationPlaybackPort
    {
        internal Func<Task> Stop { get; set; } = stop;
        public Task StopPlaybackForMutationAsync() => Stop();
    }

    private static PackageCatalogWorkflowOwner CreateOwner(
        Func<BMSLibrary> libraryProvider,
        List<string> events,
        IPackageCatalogStore store,
        IUiDialogService dialogs,
        RecordingPhaseObserver? phaseObserver = null)
    {
        phaseObserver ??= new RecordingPhaseObserver(events);
        ChartMutationActivityOwner activity = new();
        var owner = new PackageCatalogWorkflowOwner(
            libraryProvider,
            new ChartFileOperationSynchronizer(),
            activity,
            dialogs,
            new NoOpChartMutationPlaybackPort(),
            mutation => Task.Run(mutation),
            store);
        owner.MutationPhasePublished += phaseObserver.OnPhasePublished;
        activity.ActivityChanged += phaseObserver.OnActivityChanged;
        return owner;
    }

    private static FakeUiDialogService AcceptedDialogs()
    {
        return new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
        };
    }

    private static BMSLibrary CreateLibrary()
    {
        return (BMSLibrary)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSLibrary));
    }

    private static ChartOperationTarget CreateTarget()
    {
        var chart = (ChartFile)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ChartFile));
        return new ChartOperationTarget(
            chart,
            null,
            ChartOperationSourceScope.PendingPackage,
            isOwned: false,
            isPending: true,
            isPlaylistMissing: false,
            ChartOperationCapabilities.UpdateInstallDestination);
    }

    private sealed class RecordingPhaseObserver
    {
        private readonly List<string> events;

        internal RecordingPhaseObserver(List<string> events)
        {
            this.events = events;
        }

        internal Exception? EndActivityFailure { get; set; }

        internal void OnActivityChanged(object? sender, EventArgs e)
        {
            var activity = (ChartMutationActivityOwner)sender!;
            events.Add(activity.IsActive ? "activity-start" : "activity-end");
            if (!activity.IsActive && EndActivityFailure != null)
            {
                throw EndActivityFailure;
            }
        }

        internal void OnPhasePublished(object? sender, PackageCatalogMutationPhaseEventArgs e)
        {
            events.Add(e.Phase switch
            {
                PackageCatalogMutationPhase.RefreshSuppressionStarted => "suppression-start",
                PackageCatalogMutationPhase.RefreshSuppressionEnded => "suppression-end",
                _ => throw new ArgumentOutOfRangeException(nameof(e.Phase), e.Phase, "Unsupported package catalog mutation phase.")
            });
        }
    }

    private sealed class RecordingStore : IPackageCatalogStore
    {
        private readonly List<string> events;

        internal RecordingStore(List<string> events)
        {
            this.events = events;
        }

        internal int RemoveAllCount { get; private set; }

        internal int RemovePackagesCount { get; private set; }

        internal PackageCatalogSection LastSection { get; private set; }

        internal IReadOnlyList<ChartPackage> LastPackages { get; private set; } = [];

        internal IReadOnlyList<ChartOperationTarget> LastTargets { get; private set; } = [];

        internal IReadOnlyList<ChartPackage> ResolvedPackages { get; set; } = [];

        internal PackageCatalogSection? EmptySection { get; set; }

        internal Exception? EmptySectionFailure { get; set; }

        internal Exception? Failure { get; set; }

        public Task RemoveAllAsync(BMSLibrary library, PackageCatalogSection section)
        {
            events.Add("store-remove-all");
            RemoveAllCount++;
            LastSection = section;
            ThrowIfConfigured();
            return Task.CompletedTask;
        }

        public Task RemovePackagesAsync(
            BMSLibrary library,
            PackageCatalogSection section,
            IReadOnlyList<ChartPackage> packages)
        {
            events.Add("store-remove-packages");
            RemovePackagesCount++;
            LastSection = section;
            LastPackages = packages;
            ThrowIfConfigured();
            return Task.CompletedTask;
        }

        public IReadOnlyList<ChartPackage> ResolvePackages(
            BMSLibrary library,
            PackageCatalogSection section,
            IReadOnlyList<ChartOperationTarget> targets)
        {
            events.Add("store-resolve");
            LastSection = section;
            LastTargets = targets;
            return ResolvedPackages;
        }

        public bool IsSectionEmpty(BMSLibrary library, PackageCatalogSection section)
        {
            if (EmptySectionFailure != null)
            {
                throw EmptySectionFailure;
            }
            return EmptySection == section;
        }

        private void ThrowIfConfigured()
        {
            if (Failure != null)
            {
                throw Failure;
            }
        }
    }

    private sealed class FakeUiDialogService : IUiDialogService
    {
        internal UiDialogResult? ConfirmationResult { get; set; }

        internal UiConfirmationRequest? ConfirmationRequest { get; private set; }

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            ConfirmationRequest = request;
            return Task.FromResult(ConfirmationResult
                ?? throw new InvalidOperationException("Confirmation result was not configured."));
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request, CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
