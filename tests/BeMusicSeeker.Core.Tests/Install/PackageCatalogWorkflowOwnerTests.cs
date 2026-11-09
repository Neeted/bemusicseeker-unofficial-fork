using System.Collections.ObjectModel;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.TestSupport.Components.PackageCatalogWorkflowOwnerTestSupport;

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
    [TestMethod]
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

    /// <summary>借用保留だけの全解除と導入済み記録の解除は、再生停止へ入らず実体を保持します。</summary>
    [TestMethod]
    [DataRow((int)PackageCatalogSection.Pending)]
    [DataRow((int)PackageCatalogSection.Installed)]
    public async Task RecordOnlyRemoval_DoesNotStopPlayback(int sectionValue)
    {
        var databaseScope = new ApplicationDatabaseScope();
        using var caseWorkspace1 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(caseWorkspace1.RootDirectory, "bms-catalog-record-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        TestBmsLibrary? library = null;
        try
        {
            string db = Path.Combine(root, "song.db");
            var gateway = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), db);
            gateway.EnsureAppOwnedSchema();
            gateway.EnsureLibraryStartupSchema();
            library = new TestBmsLibrary(databaseScope, db, null, null, new ResilientFileMutationService(),
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

    /// <summary>実導入Pのa/bと資源のみ導入Qのaを行へ投影し、選んだ所属だけを除きます。同tokenの別所属への誤解決・退役行の再結合・所持実体の削除を検出します。</summary>
    [TestMethod]
    [DataRow("q")]
    [DataRow("p")]
    [DataRow("both")]
    [DataRow("siblings")]
    [DataRow("retired")]
    public async Task RemoveSelectionAsync_ResolvesActualInstalledMembership(string selection)
    {
        await InstallServiceComponentTestSupport.WithTemporarySongDbAsync(async (dbPath, root) =>
        {
            var scope = new ApplicationDatabaseScope();
            string install = Directory.CreateDirectory(Path.Combine(root, "Library")).FullName;
            string pSource = Directory.CreateDirectory(Path.Combine(root, "P")).FullName;
            string qSource = Directory.CreateDirectory(Path.Combine(root, "Q")).FullName;
            const string a = "#TITLE Song\n#ARTIST Artist\n#GENRE a\n#WAV01 sound.wav\n#00111:01\n";
            File.WriteAllText(Path.Combine(pSource, "a.bms"), a);
            File.WriteAllText(Path.Combine(pSource, "b.bms"), a + "#PLAYLEVEL 2\n");
            File.WriteAllText(Path.Combine(pSource, "sound.wav"), "original");
            File.WriteAllText(Path.Combine(qSource, "a.bms"), a);
            File.WriteAllText(Path.Combine(qSource, "extra.wav"), "additional");
            var p = ChartPackage.FromChartEntries([PackageChartEntry.FromPath(Path.Combine(pSource, "a.bms")), PackageChartEntry.FromPath(Path.Combine(pSource, "b.bms"))]);
            p.path = pSource;
            var q = ChartPackage.FromChartEntries([PackageChartEntry.FromPath(Path.Combine(qSource, "a.bms"))]); q.path = qSource;
            var library = new TestBmsLibrary(scope, dbPath, null, null, new InstallServiceComponentTestSupport.RealFileMutationService(), new FileDbReportRecordingDialogs(),
                new TestUiScheduler(() => null), () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false,
                    BMSInstallDir = install,
                    FolderNameFormat = "%TITLE%",
                    DeletePendingPackageSourceAfterInstall = false
                }, settings: TestBmsFactory.CreateRootSettings([install]))
            { BmsCharts = [], BmsonCharts = [], ChartPackagesPending = [p], ChartPackagesInstalled = [] };
            try
            {
                library.ForceInstallPendingPackages([p], approveNormalInstallOverride: true, notices: new LibraryOperationNotices());
                Assert.AreEqual(2, library.BmsCharts.Count());
                string destination = Path.GetDirectoryName(library.BmsCharts.First().Path) ?? throw new AssertFailedException("導入先が必要です。");
                library.ChartPackagesPending = [q];
                foreach (PackageChartEntry entry in q.ChartEntries) { BmsLibraryPackageInstallService.InitializePendingResourceHealth(entry); }
                Assert.IsTrue(library.SetPendingInstallDestination(q.ChartEntries.Single(), destination, false));
                PendingInstallBatchResult installed = library.InstallPendingPackagesToEstimatedDestinationsWithReceipt([q], new LibraryOperationNotices());
                Assert.IsTrue(installed.HasDurableCommit);
                Assert.AreEqual(2, library.ChartPackagesInstalled.Count);
                ChartPackage installedP = library.ChartPackagesInstalled.Single(package => package.ChartEntries.Count == 2);
                ChartPackage installedQ = library.ChartPackagesInstalled.Single(package => package.ChartEntries.Count == 1);
                ChartFile[] before = library.BmsCharts.ToArray();
                Dictionary<string, byte[]> bytes = before.ToDictionary(chart => chart.Path, chart => File.ReadAllBytes(chart.Path));
                var projector = new MainChartRowProjectionOwner();
                ChartOperationTarget Target(PackageChartEntry entry)
                {
                    LibraryChartRow row = projector.CreatePackageRow(library, entry);
                    Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, ChartOperationSourceScope.NewlyInstalledPackage, out ChartOperationTarget target));
                    return target;
                }
                ChartOperationTarget qTarget = Target(installedQ.ChartEntries.Single());
                ChartOperationTarget[] targets = selection switch
                {
                    "q" or "retired" => [qTarget],
                    "p" => [Target(installedP.ChartEntries[0])],
                    "siblings" => [.. installedP.ChartEntries.Select(Target)],
                    _ => [Target(installedP.ChartEntries[0]), qTarget]
                };
                var owner = new PackageCatalogWorkflowOwner(() => library, library.OperationAdmission, new ChartMutationActivityOwner(),
                    AcceptedDialogs(), new NoOpChartMutationPlaybackPort(), mutation => Task.Run(mutation), new BmsLibraryPackageCatalogStore(() => Task.CompletedTask));
                if (selection == "retired") { Assert.IsTrue((await owner.RemovePackageAsync(PackageCatalogSection.Installed, installedQ)).Succeeded); }
                PackageCatalogMutationResult result = await owner.RemoveSelectionAsync(PackageCatalogRemovalRequest.CreateInstalled(targets));
                Assert.IsTrue(result.Succeeded);
                Assert.AreEqual(selection is "q" or "retired", library.ChartPackagesInstalled.Contains(installedP));
                Assert.AreEqual(selection is "p" or "siblings", library.ChartPackagesInstalled.Contains(installedQ));
                Assert.AreEqual(2, library.BmsCharts.Count());
                foreach (ChartFile chart in before)
                {
                    CollectionAssert.AreEqual(bytes[chart.Path], File.ReadAllBytes(chart.Path));
                    Assert.AreSame(chart.Token, library.BmsCharts.Single(current => current.Path == chart.Path).Token);
                    Assert.IsTrue(library.TryGetInstalledDirectoryByHash(chart.Md5, out string directory));
                    Assert.AreEqual(destination, directory);
                }
                Assert.IsTrue(File.Exists(Path.Combine(destination, "extra.wav")));
                using LR2SongDBExtended db = new BmsLibraryDbGateway(scope, new BmsDatabaseLoadOptions(true, true, true), dbPath).OpenSongDbReadOnly();
                CollectionAssert.AreEquivalent(before.Select(chart => chart.Path).ToArray(), db.Table<BeMusicSeeker.Models.LR2.LR2SongDB.song>().Select(row => row.path).ToArray());
            }
            finally { library.RequestShutdown("package-membership-removal"); }
        });
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

    private static BMSLibrary CreateLibrary()
    {
        return UninitializedBmsLibrary.Create();
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

}
