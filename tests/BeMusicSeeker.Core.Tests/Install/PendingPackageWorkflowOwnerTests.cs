using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.TestSupport.Components.PendingPackageStoreTestFixture;
using static BeMusicSeeker.TestSupport.Components.PendingPackageWorkflowOwnerTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed partial class PendingPackageWorkflowOwnerTests
{
    /// <summary>同一推定対象群に確定DSTが残る間は既存skipを保ち、全DSTの明示クリア終端後の次の明示推定だけが両項目へ新宛先を適用します。</summary>
    /// <remarks>成功facts後の混在状態を入力し、実owner/storeのTask・表示公開・L終端と保留記録保持を確認します。失効factsの生成と元健康度12値の条件表は既存系列へ分担します。</remarks>
    [TestMethod]
    public async Task SearchPackagesAsync_PartiallyConfiguredPackageRequiresExplicitClearBeforeReestimation()
    {
        var databaseScope = new ApplicationDatabaseScope();
        using var caseWorkspace1 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(caseWorkspace1.RootDirectory, nameof(PendingPackageWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        TestBmsLibrary? library = null;
        PendingPackageWorkflowOwner? owner = null;
        RecordingPresentation? presentation = null;
        EventHandler<PendingPackageWorkflowChangedEventArgs>? capturePublication = null;
        Task? operation = null;
        Exception? primaryFailure = null;
        try
        {
            string source = Path.Combine(root, "Pending");
            string libraryRoot = Path.Combine(root, "Library");
            string manual = Path.Combine(libraryRoot, "Manual");
            string strong = Path.Combine(libraryRoot, "Strong");
            foreach (string directory in new[] { source, manual, strong }) { Directory.CreateDirectory(directory); }
            const string resources = "\n#WAV01 sound.wav\n#00111:01\n";
            string[] inputPaths = [Path.Combine(source, "first.bms"), Path.Combine(source, "second.bms")];
            File.WriteAllText(inputPaths[0], "#TITLE Target Song\n#ARTIST Target Artist\n#GENRE first" + resources);
            File.WriteAllText(inputPaths[1], "#TITLE Target Song\n#ARTIST Target Artist\n#GENRE second" + resources);
            string manualChartPath = Path.Combine(manual, "owned.bms");
            string strongChartPath = Path.Combine(strong, "owned.bms");
            File.WriteAllText(manualChartPath, "#TITLE Unrelated Work\n#ARTIST Different Composer" + resources);
            File.WriteAllText(strongChartPath, "#TITLE Target Song\n#ARTIST Target Artist\n#GENRE owned" + resources);
            File.WriteAllText(Path.Combine(manual, "sound.wav"), "manual audio");
            File.WriteAllText(Path.Combine(strong, "sound.wav"), "strong audio");
            PackageChartEntry[] entries = [.. inputPaths.Select(PackageChartEntry.FromPath)];
            foreach (PackageChartEntry entry in entries) { BmsLibraryPackageInstallService.InitializePendingResourceHealth(entry); }
            var package = ChartPackage.FromChartEntries(entries);
            package.path = source;
            package.delete_parent = false;
            string songDbPath = Path.Combine(root, "song.db");
            File.WriteAllBytes(songDbPath, []);
            ChartFile[] owned = [.. new[] { manualChartPath, strongChartPath }
                .Select(path => BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path)))];
            var currentLibrary = new TestBmsLibrary(databaseScope, songDbPath, null, null, new ResilientFileMutationService(), new RecordingLibraryDialogService(),
                new TestUiScheduler(() => null), () => new BmsLibraryOptionsSnapshot
                { OperationModeLR2DB = false, InstallDestinationAutoApplyMinAudioHealth = 100 },
                settings: TestBmsFactory.CreateRootSettings([libraryRoot]))
            { BmsCharts = owned, BmsonCharts = [], ChartPackagesPending = [package], ChartPackagesInstalled = [] };
            library = currentLibrary;
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(databaseScope, songDbPath, db =>
            {
                db.CreateTable<LR2SongDBExtended.install>();
                db.InsertOrReplace(package, typeof(LR2SongDBExtended.install));
                foreach (ChartFile chart in owned) { db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(chart), typeof(LR2SongDB.song)); }
            });
            var index = new LibraryResourceIndex();
            index.DirectoryLookupCache.AddDir(manual, ["sound.wav"]);
            index.DirectoryLookupCache.AddDir(strong, ["sound.wav"]);
            LibraryResourceIndexTestSupport.GetOwner(currentLibrary).Replace(index);
            // 失効済み項目は元配置0%、scope外の確定DSTは旧Manual配置100%を保持する入力です。
            entries[0].RestoreInstallDestinationState(new PackageChartInstallDestinationState(manual, "Unrelated Work", "Different Composer", []));
            BmsLibraryPackageInstallService.RefreshPendingResourceHealth([entries[0]], package, index.DirectoryLookupCache);
            BmsLibraryPackageInstallService.ClearPendingInstallDestination(entries[1]);
            Assert.AreEqual(100, entries[0].Chart.WAVHealth);
            Assert.AreEqual(0, entries[1].Chart.WAVHealth);
            (ChartWarningKind Kind, string Message)[][] beforeWarnings = entries.Select(entry => entry.Chart.Warnings.Select(warning => (warning.Kind, warning.Message)).ToArray()).ToArray();
            var events = new List<string>();
            presentation = new RecordingPresentation(events);
            PendingPackageWorkflowOwner currentOwner = CreateOwner(() => currentLibrary, events, new BmsLibraryPendingPackageStore(), AcceptedDialogs(),
                presentation: presentation, chartFileOperations: currentLibrary.OperationAdmission, playback: new NoOpChartMutationPlaybackPort());
            owner = currentOwner;
            PendingPackageMutationAppliedEventArgs? publication = null;
            capturePublication = (_, args) => { if (args is PendingPackageMutationAppliedEventArgs applied) { publication = applied; } };
            currentOwner.WorkflowChanged += capturePublication;
            AssertPackageAndRecord();

            await RunAndAwait(() => currentOwner.SearchPackagesAsync([package]));
            Assert.AreEqual(manual, entries[0].Chart.InstallDestination);
            Assert.AreEqual("Unrelated Work", entries[0].Chart.InstallDestinationTitle);
            Assert.AreEqual("Different Composer", entries[0].Chart.InstallDestinationArtist);
            Assert.AreEqual(ChartResourcePlacementBasis.Destination, entries[0].ResourcePlacement.Basis);
            Assert.AreEqual(manual, entries[0].ResourcePlacement.Directory);
            Assert.AreEqual(100, entries[0].Chart.WAVHealth);
            Assert.AreEqual(string.Empty, entries[1].Chart.InstallDestination);
            Assert.AreEqual(string.Empty, entries[1].Chart.InstallDestinationTitle);
            Assert.AreEqual(string.Empty, entries[1].Chart.InstallDestinationArtist);
            Assert.AreEqual(ChartResourcePlacementBasis.Current, entries[1].ResourcePlacement.Basis);
            Assert.AreEqual(source, entries[1].ResourcePlacement.Directory);
            Assert.AreEqual(0, entries[1].Chart.WAVHealth);
            for (int i = 0; i < entries.Length; i++)
            {
                Assert.AreEqual(0, entries[i].Chart.InstallDestinationSuggestions.Count);
                CollectionAssert.AreEqual(beforeWarnings[i], entries[i].Chart.Warnings.Select(warning => (warning.Kind, warning.Message)).ToArray());
            }

            await RunAndAwait(() => currentOwner.ClearPackagesAsync([package]));
            foreach (PackageChartEntry entry in entries)
            {
                Assert.AreEqual(string.Empty, entry.Chart.InstallDestination);
                Assert.AreEqual(string.Empty, entry.Chart.InstallDestinationTitle);
                Assert.AreEqual(string.Empty, entry.Chart.InstallDestinationArtist);
                Assert.AreEqual(0, entry.Chart.InstallDestinationSuggestions.Count);
                Assert.IsFalse(entry.Chart.Warnings.Any(warning => warning.Category == ChartWarningCategory.InstallEstimation));
                Assert.AreEqual(ChartResourcePlacementBasis.Current, entry.ResourcePlacement.Basis);
                Assert.AreEqual(source, entry.ResourcePlacement.Directory);
                Assert.AreEqual(0, entry.Chart.WAVHealth);
            }

            await RunAndAwait(() => currentOwner.SearchPackagesAsync([package]));
            foreach (PackageChartEntry entry in entries)
            {
                Assert.AreEqual(strong, entry.Chart.InstallDestination);
                Assert.AreEqual("Target Song", entry.Chart.InstallDestinationTitle);
                Assert.AreEqual("Target Artist", entry.Chart.InstallDestinationArtist);
                Assert.AreEqual(ChartResourcePlacementBasis.Destination, entry.ResourcePlacement.Basis);
                Assert.AreEqual(strong, entry.ResourcePlacement.Directory);
                Assert.AreEqual(100, entry.Chart.WAVHealth);
                Assert.AreEqual(0, entry.Chart.InstallDestinationSuggestions.Count);
                Assert.IsFalse(entry.Chart.Warnings.Any(warning => warning.Category == ChartWarningCategory.InstallEstimation));
                Assert.IsFalse(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            }

            async Task RunAndAwait(Func<Task> start)
            {
                publication = null;
                operation = start();
                await operation;
                PendingPackageMutationAppliedEventArgs applied = publication ?? throw new AssertFailedException("今回の操作結果を公開します。");
                await applied.Presentation;
                await currentLibrary.OperationAdmission.WaitForIdleAsync();
                Assert.IsFalse(currentLibrary.OperationAdmission.IsActive);
                Assert.IsFalse(entries.Any(entry => entry.Chart.Status.HasFlag(ChartFileStatus.SEARCHING)));
                AssertPackageAndRecord();
            }

            void AssertPackageAndRecord()
            {
                Assert.AreSame(package, currentLibrary.ChartPackagesPending.Single());
                CollectionAssert.AreEqual(entries, package.ChartEntries.ToArray());
                CollectionAssert.AreEqual(inputPaths, entries.Select(entry => entry.Chart.Path).ToArray());
                Assert.IsTrue(inputPaths.All(File.Exists));
                Assert.AreEqual(0, currentLibrary.ChartPackagesInstalled.Count);
                using LR2SongDBExtended readback = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
                Assert.AreEqual(source, readback.Table<LR2SongDBExtended.install>().Single().path);
            }
        }
        catch (Exception failure) { primaryFailure = failure; throw; }
        finally
        {
            var cleanupFailures = new List<Exception>();
            try { if (operation is { IsCompleted: false }) { await operation; } }
            catch (Exception failure) { cleanupFailures.Add(failure); }
            if (owner != null)
            {
                if (capturePublication != null) { owner.WorkflowChanged -= capturePublication; }
                if (presentation != null) { owner.WorkflowChanged -= presentation.OnWorkflowChanged; }
            }
            try { library?.RequestShutdown("partially-configured-pending-estimation-test"); }
            catch (Exception failure) { cleanupFailures.Add(failure); }
            try { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
            catch (Exception failure) { cleanupFailures.Add(failure); }
            if (cleanupFailures.Count > 0)
            {
                if (primaryFailure != null) { cleanupFailures.Insert(0, primaryFailure); }
                throw new AggregateException(cleanupFailures);
            }
        }
    }

    /// <summary>修復sessionの反映失敗を受付とactivity解放後に一度報告し、無関係な後続例外は元例外で伝えて再報告しない。</summary>
    [TestMethod]
    public async Task FixInstalledLocationsAsync_ReportsSingleRepairSessionAfterRelease()
    {
        var events = new List<string>();
        var catalogFailure = new IOException("repair catalog failure");
        var sessionReceipt = new LibraryMutationSessionReceipt(
            [
                new LibraryMutationSessionTarget("moved.bms", "installed\\moved.bms"),
                new LibraryMutationSessionTarget("removed.bms", string.Empty)
            ],
            durableCommit: false,
            catalogChartRemovalCount: 1,
            catalogChartPathChangeCount: 1,
            applyFailure: catalogFailure);
        var repairResult = new LibraryFixInstallationResult
        {
            SessionReceipt = sessionReceipt,
            Failure = catalogFailure,
            MovedCount = 1,
            ApprovedRemovedCount = 1
        };
        var store = new RecordingStore(events) { RepairResult = repairResult };
        var gate = new ChartFileOperationSynchronizer();
        bool releasedAtReport = false;
        var dialogs = new FileDbReportRecordingDialogs
        {
            OnMessage = () =>
            {
                releasedAtReport = gate.TryEnter(out IDisposable lease) && events.Contains("activity-end");
                lease?.Dispose();
            }
        };
        PendingPackageWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, dialogs, chartFileOperations: gate);
        ChartFile chart = CreateChart(installDestination: @"C:\Installed");
        Assert.IsTrue(RepairInstalledLocationRequest.TryCreate(
            [CreateTarget(chart, ChartOperationCapabilities.RepairInstalledLocation)], out RepairInstalledLocationRequest? request));

        await owner.FixInstalledLocationsAsync(request);

        Assert.IsTrue(releasedAtReport);
        Assert.AreEqual(1, dialogs.Messages.Count);
        Assert.AreEqual(System.Windows.MessageBoxImage.Error, dialogs.Messages.Single().Icon);
        Assert.AreEqual(1, events.Count(value => value == "store-fix-installed-locations"));
        store.RepairFailure = new IOException("unrelated failure");
        Exception unrelated = await Assert.ThrowsExactlyAsync<IOException>(() => owner.FixInstalledLocationsAsync(request));
        Assert.AreSame(store.RepairFailure, unrelated);
        Assert.AreEqual(1, dialogs.Messages.Count);
    }

    /// <summary>修復の対象別失敗を一度報告し、任意の報告表示が失敗してもactivityの終端を止めない。</summary>
    [TestMethod]
    public async Task FixInstalledLocationsAsync_ReportsRepairItemFailureWithoutStoppingContinuation()
    {
        var events = new List<string>();
        var itemFailure = new IOException("repair target not confirmed");
        var sessionReceipt = new LibraryMutationSessionReceipt(
            [],
            durableCommit: false,
            itemFailures:
            [
                new LibraryMutationSessionItemFailure(
                    new LibraryMutationSessionTarget("unverified.bms", string.Empty),
                    itemFailure)
            ]);
        var store = new RecordingStore(events)
        {
            RepairResult = new LibraryFixInstallationResult
            {
                SessionReceipt = sessionReceipt
            }
        };
        var dialogs = new FileDbReportRecordingDialogs { MessageFailure = new IOException("optional report failure") };
        PendingPackageWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, dialogs);
        Assert.IsTrue(RepairInstalledLocationRequest.TryCreate(
            [CreateTarget(CreateChart(installDestination: @"C:\Installed"), ChartOperationCapabilities.RepairInstalledLocation)], out RepairInstalledLocationRequest? request));
        await owner.FixInstalledLocationsAsync(request);
        Assert.AreEqual(1, dialogs.Messages.Count);
        Assert.AreEqual(1, events.Count(value => value == "store-fix-installed-locations"));
        Assert.IsTrue(events.Contains("activity-end"));
    }

    /// <summary>移動確定後の保守失敗も受付とactivity解放後に一度報告し、後続失敗の原因を案内へ残す。</summary>
    [TestMethod]
    public async Task FixInstalledLocationsAsync_ReportsMovementReceiptAndLaterFailureAfterRelease()
    {
        var events = new List<string>();
        var laterFailure = new IOException("repair maintenance failure");
        var sessionReceipt = new LibraryMutationSessionReceipt(
            [new LibraryMutationSessionTarget("source.bms", "installed\\source.bms")],
            durableCommit: true,
            catalogChartPathChangeCount: 1,
            finalizationFailure: laterFailure);
        var repairResult = new LibraryFixInstallationResult
        {
            SessionReceipt = sessionReceipt,
            Failure = laterFailure
        };
        var store = new RecordingStore(events) { RepairResult = repairResult };
        var gate = new ChartFileOperationSynchronizer();
        bool releasedAtReport = false;
        var dialogs = new FileDbReportRecordingDialogs
        {
            OnMessage = () =>
            {
                releasedAtReport = gate.TryEnter(out IDisposable lease) && events.Contains("activity-end");
                lease?.Dispose();
            }
        };
        PendingPackageWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, dialogs, chartFileOperations: gate);
        Assert.IsTrue(RepairInstalledLocationRequest.TryCreate(
            [CreateTarget(CreateChart(installDestination: @"C:\Installed"), ChartOperationCapabilities.RepairInstalledLocation)], out RepairInstalledLocationRequest? request));

        await owner.FixInstalledLocationsAsync(request);

        Assert.IsTrue(releasedAtReport);
        Assert.AreEqual(1, dialogs.Messages.Count);
        Assert.AreEqual(System.Windows.MessageBoxImage.Error, dialogs.Messages.Single().Icon);
        StringAssert.Contains(dialogs.Messages.Single().MessageBoxText, laterFailure.Message);
    }

    /// <summary>導入先を開く操作は保留区画と有効な対象権限に限定し、playlist行・不明対象・権限のない選択へ操作を広げない。</summary>
    [TestMethod]
    public void CanOpenInstallDestination_RequiresPendingSectionAndEffectiveTargetCapability()
    {
        PendingPackageWorkflowOwner owner = CreateOwner(
            () => null!,
            [],
            new RecordingStore([]),
            AcceptedDialogs());
        ChartOperationTarget pendingTarget = CreateTarget(CreateChart());
        ChartOperationTarget unsupportedTarget = CreateTarget(
            CreateChart(),
            ChartOperationCapabilities.None);
        ChartOperationTarget missingTarget = new(
            CreateChart(),
            null,
            ChartOperationSourceScope.PlaylistMissing,
            isOwned: false,
            isPending: false,
            isPlaylistMissing: true,
            ChartOperationCapabilities.UpdateInstallDestination);

        Assert.IsTrue(owner.CanOpenInstallDestination(
            pendingTarget,
            [pendingTarget],
            isPendingSection: true,
            isPlaylistRow: false));
        Assert.IsTrue(owner.CanOpenInstallDestination(
            pendingTarget,
            [],
            isPendingSection: true,
            isPlaylistRow: false));
        Assert.IsFalse(owner.CanOpenInstallDestination(
            pendingTarget,
            [unsupportedTarget],
            isPendingSection: true,
            isPlaylistRow: false));
        Assert.IsFalse(owner.CanOpenInstallDestination(
            pendingTarget,
            [pendingTarget],
            isPendingSection: false,
            isPlaylistRow: false));
        Assert.IsFalse(owner.CanOpenInstallDestination(
            pendingTarget,
            [pendingTarget],
            isPendingSection: true,
            isPlaylistRow: true));
        Assert.IsFalse(owner.CanOpenInstallDestination(
            missingTarget,
            [missingTarget],
            isPendingSection: true,
            isPlaylistRow: false));
    }

    /// <summary>存在する明示導入先はそのディレクトリを一度開き、不要な案内を表示しない。</summary>
    [TestMethod]
    public async Task OpenInstallDestinationForChartsAsync_UsesDirectDestinationAndOpensIt()
    {

        using var caseWorkspace2 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string temporaryDirectory = Path.Combine(caseWorkspace2.RootDirectory, nameof(PendingPackageWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var openedDirectories = new List<string>();
            FakeUiDialogService dialogs = AcceptedDialogs();
            PendingPackageWorkflowOwner owner = CreateOwner(
                () => null!,
                [],
                new RecordingStore([]),
                dialogs,
                directory =>
                {
                    openedDirectories.Add(directory);
                    return new ExplorerOpenResult { Kind = ExplorerOpenResultKind.OpenedDirectory };
                });

            await owner.OpenInstallDestinationForChartsAsync(
                [CreateTarget(CreateChart(installDestination: temporaryDirectory))]);

            CollectionAssert.AreEqual(new[] { temporaryDirectory }, openedDirectories);
            Assert.AreEqual(0, dialogs.MessageRequests.Count);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    /// <summary>明示導入先が消失しても所持ハッシュ探索へ読み替えず、元の明示パスを含む不在案内だけを表示する。</summary>
    [TestMethod]
    public async Task OpenInstallDestinationForChartsAsync_StaleDirectDestinationDoesNotUseHashFallback()
    {

        int providerCallCount = 0;
        FakeUiDialogService dialogs = AcceptedDialogs();
        var openedDirectories = new List<string>();
        PendingPackageWorkflowOwner owner = CreateOwner(
            () =>
            {
                providerCallCount++;
                return null!;
            },
            [],
            new RecordingStore([]),
            dialogs,
            directory =>
            {
                openedDirectories.Add(directory);
                return new ExplorerOpenResult { Kind = ExplorerOpenResultKind.OpenedDirectory };
            });

        string staleDirectory = Path.Combine(Path.GetTempPath(), nameof(PendingPackageWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        await owner.OpenInstallDestinationForChartsAsync(
            [CreateTarget(CreateChart(installDestination: staleDirectory))]);

        Assert.AreEqual(0, providerCallCount);
        Assert.AreEqual(0, openedDirectories.Count);
        Assert.AreEqual(
            string.Format(BeMusicSeeker.Properties.Resources.Msg_open_install_destination_not_found, staleDirectory),
            dialogs.MessageRequests.Single().MessageBoxText);
    }

    /// <summary>同じ導入先を持つ複数譜面を指定すると選択を制限する案内を一度表示し、存在する導入先を一度だけ開く。</summary>
    [TestMethod]
    public async Task OpenInstallDestinationForChartsAsync_NotifiesMultipleSelectionBeforeOpeningFirstTarget()
    {

        using var caseWorkspace4 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string temporaryDirectory = Path.Combine(caseWorkspace4.RootDirectory, nameof(PendingPackageWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var openedDirectories = new List<string>();
            FakeUiDialogService dialogs = AcceptedDialogs();
            PendingPackageWorkflowOwner owner = CreateOwner(
                () => null!,
                [],
                new RecordingStore([]),
                dialogs,
                directory =>
                {
                    openedDirectories.Add(directory);
                    return new ExplorerOpenResult { Kind = ExplorerOpenResultKind.OpenedDirectory };
                });

            await owner.OpenInstallDestinationForChartsAsync(
                [
                    CreateTarget(CreateChart(installDestination: temporaryDirectory)),
                    CreateTarget(CreateChart(path: @"C:\Charts\second.bms", installDestination: temporaryDirectory))
                ]);

            Assert.AreEqual(1, dialogs.MessageRequests.Count);
            Assert.AreEqual(
                BeMusicSeeker.Properties.Resources.Msg_open_install_destination_multiple_selected,
                dialogs.MessageRequests[0].MessageBoxText);
            CollectionAssert.AreEqual(new[] { temporaryDirectory }, openedDirectories);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    /// <summary>パッケージの先頭譜面に導入先がなくても、後続の解決可能なentryから存在する導入先を開く。</summary>
    [TestMethod]
    public async Task OpenInstallDestinationForPackageAsync_UsesFirstResolvableChartEntry()
    {

        using var caseWorkspace5 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string temporaryDirectory = Path.Combine(caseWorkspace5.RootDirectory, nameof(PendingPackageWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var package = ChartPackage.FromChartEntries(
            [
                PackageChartEntry.FromChart(CreateChart()),
                PackageChartEntry.FromChart(CreateChart(
                    path: @"C:\Charts\second.bms",
                    installDestination: temporaryDirectory))
            ]);
            var openedDirectories = new List<string>();
            FakeUiDialogService dialogs = AcceptedDialogs();
            PendingPackageWorkflowOwner owner = CreateOwner(
                () => null!,
                [],
                new RecordingStore([]),
                dialogs,
                directory =>
                {
                    openedDirectories.Add(directory);
                    return new ExplorerOpenResult { Kind = ExplorerOpenResultKind.OpenedDirectory };
                });

            await owner.OpenInstallDestinationForPackageAsync(package);

            CollectionAssert.AreEqual(new[] { temporaryDirectory }, openedDirectories);
            Assert.AreEqual(0, dialogs.MessageRequests.Count);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    /// <summary>パッケージの全entryに導入先がない場合はExplorerを起動せず、導入先未設定の案内を一度表示する。</summary>
    [TestMethod]
    public async Task OpenInstallDestinationForPackageAsync_AllEntriesMissingShowsWarning()
    {

        var package = ChartPackage.FromChartEntries(
        [
            PackageChartEntry.FromChart(CreateChart()),
            PackageChartEntry.FromChart(CreateChart(path: @"C:\Charts\second.bms"))
        ]);
        FakeUiDialogService dialogs = AcceptedDialogs();
        var openedDirectories = new List<string>();
        PendingPackageWorkflowOwner owner = CreateOwner(
            () => null!,
            [],
            new RecordingStore([]),
            dialogs,
            directory =>
            {
                openedDirectories.Add(directory);
                return new ExplorerOpenResult { Kind = ExplorerOpenResultKind.OpenedDirectory };
            });

        await owner.OpenInstallDestinationForPackageAsync(package);

        Assert.AreEqual(0, openedDirectories.Count);
        Assert.AreEqual(
            BeMusicSeeker.Properties.Resources.Msg_open_install_destination_missing,
            dialogs.MessageRequests.Single().MessageBoxText);
    }

    /// <summary>導入先案内の表示失敗は元例外を保持して伝え、Explorerの起動へ進まない。</summary>
    [TestMethod]
    public async Task OpenInstallDestinationForChartsAsync_DialogFailurePropagatesWithoutOpeningExplorer()
    {

        var failure = new InvalidOperationException("dialog failed");
        FakeUiDialogService dialogs = AcceptedDialogs();
        dialogs.MessageResult = UiDialogResult.Failed(failure);
        var openedDirectories = new List<string>();
        PendingPackageWorkflowOwner owner = CreateOwner(
            () => null!,
            [],
            new RecordingStore([]),
            dialogs,
            directory =>
            {
                openedDirectories.Add(directory);
                return new ExplorerOpenResult { Kind = ExplorerOpenResultKind.OpenedDirectory };
            });

        InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => owner.OpenInstallDestinationForChartsAsync(
                [CreateTarget(CreateChart(installDestination: Path.Combine(Path.GetTempPath(), nameof(PendingPackageWorkflowOwnerTests), Guid.NewGuid().ToString("N"))))]));

        Assert.AreSame(failure, exception.InnerException);
        Assert.AreEqual(0, openedDirectories.Count);
    }

    /// <summary>入力元が実ディレクトリならディレクトリ起動だけへ渡し、ファイル選択の別経路を呼ばない。</summary>
    [TestMethod]
    public void OpenPackageSourceInExplorer_OpensExistingDirectoryOnlyThroughDirectoryRoute()
    {
        using var caseWorkspace7 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string temporaryDirectory = Path.Combine(caseWorkspace7.RootDirectory, nameof(PendingPackageWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var openedDirectories = new List<string>();
            var selectedFiles = new List<string>();
            PendingPackageWorkflowOwner owner = CreateOwner(
                () => null!,
                [],
                new RecordingStore([]),
                AcceptedDialogs(),
                directory =>
                {
                    openedDirectories.Add(directory);
                    return new ExplorerOpenResult { Kind = ExplorerOpenResultKind.OpenedDirectory };
                },
                file =>
                {
                    selectedFiles.Add(file);
                    return new ExplorerOpenResult { Kind = ExplorerOpenResultKind.SelectedFile };
                });

            owner.OpenPackageSourceInExplorer(new ChartPackage { path = temporaryDirectory });

            CollectionAssert.AreEqual(new[] { temporaryDirectory }, openedDirectories);
            Assert.AreEqual(0, selectedFiles.Count);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    /// <summary>入力元が実ファイルならファイル選択だけへ渡し、ディレクトリ起動の別経路を呼ばない。</summary>
    [TestMethod]
    public void OpenPackageSourceInExplorer_OpensExistingFileOnlyThroughFileRoute()
    {
        using var caseWorkspace8 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string temporaryDirectory = Path.Combine(caseWorkspace8.RootDirectory, nameof(PendingPackageWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        string temporaryFile = Path.Combine(temporaryDirectory, "package.zip");
        Directory.CreateDirectory(temporaryDirectory);
        File.WriteAllText(temporaryFile, string.Empty);
        try
        {
            var openedDirectories = new List<string>();
            var selectedFiles = new List<string>();
            PendingPackageWorkflowOwner owner = CreateOwner(
                () => null!,
                [],
                new RecordingStore([]),
                AcceptedDialogs(),
                directory =>
                {
                    openedDirectories.Add(directory);
                    return new ExplorerOpenResult { Kind = ExplorerOpenResultKind.OpenedDirectory };
                },
                file =>
                {
                    selectedFiles.Add(file);
                    return new ExplorerOpenResult { Kind = ExplorerOpenResultKind.SelectedFile };
                });

            owner.OpenPackageSourceInExplorer(new ChartPackage { path = temporaryFile });

            Assert.AreEqual(0, openedDirectories.Count);
            CollectionAssert.AreEqual(new[] { temporaryFile }, selectedFiles);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    /// <summary>入力元パスが空または消失している場合は、Explorerのどちらの起動経路も呼ばない。</summary>
    [TestMethod]
    public void OpenPackageSourceInExplorer_MissingOrEmptyPathIsSilent()
    {
        var openedDirectories = new List<string>();
        var selectedFiles = new List<string>();
        PendingPackageWorkflowOwner owner = CreateOwner(
            () => null!,
            [],
            new RecordingStore([]),
            AcceptedDialogs(),
            directory =>
            {
                openedDirectories.Add(directory);
                return new ExplorerOpenResult { Kind = ExplorerOpenResultKind.OpenedDirectory };
            },
            file =>
            {
                selectedFiles.Add(file);
                return new ExplorerOpenResult { Kind = ExplorerOpenResultKind.SelectedFile };
            });

        owner.OpenPackageSourceInExplorer(new ChartPackage { path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) });
        owner.OpenPackageSourceInExplorer(new ChartPackage { path = string.Empty });

        Assert.AreEqual(0, openedDirectories.Count);
        Assert.AreEqual(0, selectedFiles.Count);
    }

    /// <summary>実ファイルのExplorer選択が失敗しても、ディレクトリ起動の別経路へ意味を変えて再試行しない。</summary>
    [TestMethod]
    public void OpenPackageSourceInExplorer_DoesNotTranslateExplorerFailureIntoAnotherRoute()
    {
        using var caseWorkspace10 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string temporaryDirectory = Path.Combine(caseWorkspace10.RootDirectory, nameof(PendingPackageWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        string temporaryFile = Path.Combine(temporaryDirectory, "package.zip");
        Directory.CreateDirectory(temporaryDirectory);
        File.WriteAllText(temporaryFile, string.Empty);
        try
        {
            int directoryOpenCount = 0;
            int fileSelectCount = 0;
            PendingPackageWorkflowOwner owner = CreateOwner(
                () => null!,
                [],
                new RecordingStore([]),
                AcceptedDialogs(),
                directory =>
                {
                    directoryOpenCount++;
                    return new ExplorerOpenResult { Kind = ExplorerOpenResultKind.Failed, FailureReason = "directory_failed" };
                },
                file =>
                {
                    fileSelectCount++;
                    return new ExplorerOpenResult { Kind = ExplorerOpenResultKind.Failed, FailureReason = "file_failed" };
                });

            owner.OpenPackageSourceInExplorer(new ChartPackage { path = temporaryFile });

            Assert.AreEqual(0, directoryOpenCount);
            Assert.AreEqual(1, fileSelectCount);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    /// <summary>通常・無関係保持の方針と保留entryをstoreへ渡し、変更投影が空でも抑制・activity終端後に導入先と識別情報の更新通知を送る。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SearchPendingAsync_EmptyChangesStillPublishesTerminalRefresh(bool keepUnrelated)
    {
        var events = new List<string>();
        ChartFile chart = CreateChart();
        var store = new RecordingStore(events) { ChangedCharts = [] };
        PendingPackageWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, AcceptedDialogs());
        PendingPackageMutationAppliedEventArgs? completion = null;
        owner.WorkflowChanged += (_, args) =>
        {
            if (args is PendingPackageMutationAppliedEventArgs mutation)
            {
                completion = mutation;
            }
        };
        InstallEstimationCandidatePolicy policy = keepUnrelated ? InstallEstimationCandidatePolicy.KeepUnrelated : InstallEstimationCandidatePolicy.RelatedOnly;
        var request = PendingInstallDestinationSearchRequest.CreateInstallDestinationSearch([CreateTarget(chart)], policy);

        await owner.SearchPendingAsync(request);

        Assert.AreSame(request, store.LastSearchRequest);
        Assert.IsNotNull(store.LastSearchRequest);
        Assert.AreEqual(policy, store.LastSearchRequest.CandidatePolicy);
        Assert.AreEqual(InstallEstimationCandidatePolicy.RelatedOnly, PendingInstallDestinationSearchRequest.CreateInstallDestinationSearch([CreateTarget(chart)]).CandidatePolicy);
        Assert.IsNotNull(completion);
        Assert.AreEqual(0, completion!.ChangedCharts.Count);
        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "suppression-start",
                "store-search-pending",
                "suppression-end",
                "activity-end",
                "invalidate",
                "identity-refresh"
            },
            events);
    }

    /// <summary>通常・無関係保持の方針と単独譜面をstoreへ渡し、受付解放後に変更投影を一度公開して一覧更新へ渡す。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SearchPendingAsync_LooseTargetPublishesTransientProjectionAfterGateRelease(bool keepUnrelated)
    {
        var events = new List<string>();
        ChartFile chart = CreateChart();
        var store = new RecordingStore(events) { ChangedCharts = [chart] };
        var gate = new ChartFileOperationSynchronizer();
        PendingPackageWorkflowOwner owner = CreateOwner(
            CreateLibrary,
            events,
            store,
            AcceptedDialogs(),
            chartFileOperations: gate);
        int completionCount = 0;
        bool gateAvailableAtCompletion = false;
        owner.WorkflowChanged += (_, args) =>
        {
            if (args is not PendingPackageMutationAppliedEventArgs mutationApplied)
            {
                return;
            }
            completionCount++;
            gateAvailableAtCompletion = gate.TryEnter(out IDisposable lease);
            lease?.Dispose();
            Assert.AreEqual(1, mutationApplied.ChangedCharts.Count);
            Assert.AreSame(chart, mutationApplied.ChangedCharts[0]);
        };

        InstallEstimationCandidatePolicy policy = keepUnrelated ? InstallEstimationCandidatePolicy.KeepUnrelated : InstallEstimationCandidatePolicy.RelatedOnly;
        await owner.SearchPendingAsync(
            PendingInstallDestinationSearchRequest.CreateInstallDestinationSearch([CreateLooseTarget(chart)], policy));

        Assert.IsNotNull(store.LastSearchRequest);
        Assert.AreEqual(policy, store.LastSearchRequest.CandidatePolicy);
        Assert.AreEqual(chart.Path, store.LastSearchRequest.LooseEntries.Single().Chart.Path);
        Assert.AreEqual(1, completionCount);
        Assert.IsTrue(gateAvailableAtCompletion);
        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "suppression-start",
                "store-search-pending",
                "suppression-end",
                "activity-end",
                "transient",
                "invalidate",
                "identity-refresh"
            },
            events);
    }

    /// <summary>単一・複数パッケージと通常・無関係保持の方針をstoreへ渡し、抑制・activity終端後に更新を通知する。</summary>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public async Task SearchPackagesAsync_RunsMutation(bool keepUnrelated, bool multiplePackages)
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        FakeUiDialogService dialogs = AcceptedDialogs();
        PendingPackageWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, dialogs);
        var package = new ChartPackage();
        ChartPackage[] packages = multiplePackages ? [package, new ChartPackage()] : [package];
        InstallEstimationCandidatePolicy policy = keepUnrelated ? InstallEstimationCandidatePolicy.KeepUnrelated : InstallEstimationCandidatePolicy.RelatedOnly;

        await owner.SearchPackagesAsync(packages, policy);

        CollectionAssert.AreEqual(packages, (System.Collections.ICollection)store.LastPackages);
        Assert.AreEqual(policy, store.LastCandidatePolicy);
        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "suppression-start",
                "store-search-packages",
                "suppression-end",
                "activity-end",
                "invalidate",
                "identity-refresh"
            },
            events);
    }

    /// <summary>ライブラリ未接続でも保留集合の解除storeを呼び、抑制・activityの終端後に導入先の失効通知を送る。</summary>
    [TestMethod]
    public async Task ClearPackagesAsync_WithoutAttachedLibraryStillPublishesInvalidation()
    {
        var events = new List<string>();
        ChartFile chart = CreateChart();
        var store = new RecordingStore(events) { ChangedCharts = [chart] };
        PendingPackageWorkflowOwner owner = CreateOwner(() => null!, events, store, AcceptedDialogs());

        await owner.ClearPackagesAsync([new ChartPackage()]);

        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "suppression-start",
                "store-clear-packages",
                "suppression-end",
                "activity-end",
                "invalidate"
            },
            events);
    }

    /// <summary>所持先修正の推定ではstore処理を抑制・activityで囲み、それらの終端後に導入先の更新を通知する。</summary>
    [TestMethod]
    public async Task SearchCorrectAsync_PublishesAfterMutationBoundary()
    {
        var events = new List<string>();
        ChartFile chart = CreateChart();
        var store = new RecordingStore(events) { ChangedCharts = [chart] };
        PendingPackageWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, AcceptedDialogs());
        Assert.IsTrue(RepairInstalledLocationRequest.TryCreate(
            [CreateTarget(chart, ChartOperationCapabilities.RepairInstalledLocation)],
            out RepairInstalledLocationRequest request));

        await owner.SearchCorrectAsync(request);

        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "suppression-start",
                "store-search-correct",
                "suppression-end",
                "activity-end",
                "invalidate"
            },
            events);
    }

    /// <summary>保留entryの手動導入先をstoreへ渡し、抑制・activityの終端後に導入先の更新を通知する。</summary>
    [TestMethod]
    public async Task SetPendingAsync_RefreshesEditedRowAfterMutationBoundary()
    {
        var events = new List<string>();
        ChartFile chart = CreateChart();
        var store = new RecordingStore(events) { SetResult = chart };
        PendingPackageWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, AcceptedDialogs());
        Assert.IsTrue(PendingInstallDestinationEditRequest.TryCreate(
            CreateTarget(chart),
            out PendingInstallDestinationEditRequest request));

        await owner.SetPendingAsync(request, "C:\\Installed");

        Assert.AreEqual("C:\\Installed", store.LastDestinationDirectory);
        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "suppression-start",
                "store-set",
                "suppression-end",
                "activity-end",
                "invalidate"
            },
            events);
    }

    /// <summary>導入先セル編集は短い抑制/activityを閉じても必要表示Taskの終端まで実Lを保持し、表示失敗を伝えて解放します。終端後の明示編集だけを再受付します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SetPendingAsync_HoldsAdmissionThroughRequiredPresentation(bool failPresentation)
    {
        var events = new List<string>();
        ChartFile chart = CreateChart();
        var store = new RecordingStore(events) { SetResult = chart };
        var gate = new ChartFileOperationSynchronizer();
        PendingPackageWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, AcceptedDialogs(), chartFileOperations: gate);
        Assert.IsTrue(PendingInstallDestinationEditRequest.TryCreate(CreateTarget(chart), out PendingInstallDestinationEditRequest request));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool hold = true;
        var failure = new InvalidOperationException("required pending presentation failed");
        owner.WorkflowChanged += (_, args) =>
        {
            if (args is PendingPackageMutationAppliedEventArgs applied && hold)
            {
                Assert.AreSame(request.PackageEntry!.Chart, applied.ProjectionCharts.Single());
                applied.Presentation = Present();
            }
        };
        async Task Present()
        {
            entered.TrySetResult(); await release.Task;
            if (failPresentation) { throw failure; }
        }
        Task edit = owner.SetPendingAsync(request, @"C:\Installed");
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, edit, "pending-required-presentation");
            Assert.IsFalse(edit.IsCompleted);
            Assert.AreEqual(@"C:\Installed", store.LastDestinationDirectory);
            Assert.IsTrue(events.Contains("suppression-end"));
            Assert.IsTrue(events.Contains("activity-end"));
            Assert.IsFalse(gate.TryEnter(out IDisposable rejected)); rejected?.Dispose();
        }
        finally { hold = false; release.TrySetResult(); }
        if (failPresentation) { Assert.AreSame(failure, await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => edit)); }
        else { await edit; }
        Assert.IsTrue(gate.TryEnter(out IDisposable next)); next.Dispose();
        await owner.SetPendingAsync(request, @"C:\Next");
        Assert.AreEqual(@"C:\Next", store.LastDestinationDirectory);
    }

    /// <summary>保留導入先の消去が失敗しても抑制とactivityを閉じ、元失敗を伝えて成功の一覧更新を通知しない。</summary>
    [TestMethod]
    public async Task ClearPendingAsync_PropagatesFailureAndClosesMutationBoundary()
    {
        var events = new List<string>();
        var store = new RecordingStore(events) { Failure = new InvalidOperationException("clear failed") };
        PendingPackageWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, AcceptedDialogs());
        Assert.IsTrue(PendingInstallDestinationClearRequest.TryCreate(
            [CreateTarget(CreateChart())],
            out PendingInstallDestinationClearRequest request));

        InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => owner.ClearPendingAsync(request));

        Assert.AreEqual("clear failed", exception.Message);
        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "suppression-start",
                "store-clear-pending",
                "suppression-end",
                "activity-end"
            },
            events);
    }

    /// <summary>既存導入先への強制導入を承認すると、再生停止後に対象の変更境界を実行し、空区画と一覧反映範囲を結果へ渡す。</summary>
    [TestMethod]
    public async Task ForceInstallPackagesAsync_ApprovesOverrideThenStopsPlaybackAndMutatesPackageScope()
    {

        var events = new List<string>();
        ChartFile chart = CreateChart(installDestination: @"C:\Installed\song.bms");
        var package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(chart)]);
        var store = new RecordingStore(events)
        {
            EmptyPendingSection = true
        };
        var presentation = new RecordingPresentation(events);
        var playback = new RecordingPlayback(events);
        var dialogs = new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Yes)
        };
        PendingPackageWorkflowOwner owner = CreateOwner(
            CreateLibrary,
            events,
            store,
            dialogs,
            presentation: presentation,
            playback: playback);

        PendingPackageMutationResult result = await owner.ForceInstallPackagesAsync([package]);

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(result.Failure);
        Assert.IsTrue(result.ShouldApplyView);
        Assert.AreEqual(PackageCatalogSection.Pending, result.EmptySection);
        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "playback-stop",
                "suppression-start",
                "store-force-install",
                "suppression-end",
                "activity-end"
            },
            events);
        Assert.AreEqual(PendingPackageRefreshScope.PackageMutation, presentation.LastRefreshScope);
        Assert.IsTrue(store.ApprovedNormalInstallOverridePackages.Contains(package));
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Confirm_NormalInstallOverride, dialogs.ConfirmationRequest!.MessageBoxText);
    }

    /// <summary>既存導入先への強制導入を拒否した対象は承認集合へ含めず、storeへ対象と未承認の事実を渡して一覧反映の終端を返す。</summary>
    [TestMethod]
    public async Task ForceInstallPackagesAsync_OverrideRejectionRunsBoundaryWithoutApprovingPackage()
    {

        var events = new List<string>();
        ChartFile chart = CreateChart(installDestination: @"C:\Installed\song.bms");
        var package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(chart)]);
        var store = new RecordingStore(events);
        var dialogs = new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.No)
        };
        PendingPackageWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, dialogs);

        PendingPackageMutationResult result = await owner.ForceInstallPackagesAsync([package]);

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(result.Failure);
        Assert.IsTrue(result.ShouldApplyView);
        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "playback-stop",
                "suppression-start",
                "store-force-install",
                "suppression-end",
                "activity-end"
            },
            events);
        Assert.AreEqual(0, store.ApprovedNormalInstallOverridePackages.Count);
        Assert.AreSame(package, store.LastPackages.Single());
    }

    /// <summary>導入先有無と新規確認設定に応じて確認を行い、肯定した対象だけを承認し、未選択の閉鎖を承認に変換しない。</summary>
    // 導入先有無/新規確認ON-OFF/肯定・拒否/未選択閉鎖を分け、閉鎖応答は実表示部品の既定応答正規化へ通す。
    [TestMethod]
    [DataRow(false, true, true, false)]
    [DataRow(false, true, false, false)]
    [DataRow(false, false, false, false)]
    [DataRow(true, true, true, false)]
    [DataRow(true, true, false, false)]
    [DataRow(true, false, true, false)]
    [DataRow(true, false, false, false)]
    [DataRow(false, true, false, true)]
    [DataRow(true, true, false, true)]
    [DataRow(true, false, false, true)]
    public async Task ForceInstallPackagesAsync_ConfirmsAccordingToDestinationAndNewInstallSetting(
        bool hasDestination, bool showNewConfirmation, bool accept, bool closeWithoutSelection)
    {

        var events = new List<string>();
        ChartFile chart = CreateChart(installDestination: hasDestination ? @"C:\Installed" : null);
        var package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(chart)]);
        var store = new RecordingStore(events);
        var dialogs = new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(accept ? MessageBoxResult.Yes : MessageBoxResult.No)
        };
        UiDialogResult? closedResult = null;
        if (closeWithoutSelection)
        {
            dialogs.ConfirmationHandler = request =>
            {
                var response = UiDialogResult.ClosedByUser(
                    ThemedMessageBox.NormalizeDefaultResult(request.Button, request.DefaultResult));
                closedResult = response;
                return Task.FromResult(response);
            };
        }
        PendingPackageWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, dialogs,
            settingsProvider: () => new InstallDestinationWorkflowSettingsSnapshot(false, false, showNewConfirmation));

        PendingPackageMutationResult result = await owner.ForceInstallPackagesAsync([package]);

        Assert.IsTrue(result.Succeeded);
        bool requiresConfirmation = hasDestination || showNewConfirmation;
        Assert.AreEqual(!requiresConfirmation || accept, store.ApprovedNormalInstallOverridePackages.Contains(package));
        if (closeWithoutSelection)
        {
            Assert.IsNotNull(closedResult);
            Assert.AreEqual(UiDialogStatus.ClosedByUser, closedResult.Status);
            Assert.IsFalse(closedResult.IsPositive);
            Assert.AreSame(package, store.LastPackages.Single());
            Assert.AreEqual(chart.Path, package.ChartEntries.Single().Chart.Path);
            Assert.AreEqual(chart.InstallDestination, package.ChartEntries.Single().Chart.InstallDestination);
        }

        if (requiresConfirmation)
        {
            Assert.IsNotNull(dialogs.ConfirmationRequest);
            Assert.AreEqual(hasDestination
                ? BeMusicSeeker.Properties.Resources.Confirm_NormalInstallOverride
                : BeMusicSeeker.Properties.Resources.Confirm_NewPackageInstall, dialogs.ConfirmationRequest.MessageBoxText);
        }
        else
        {
            Assert.IsNull(dialogs.ConfirmationRequest);
        }
    }

    /// <summary>新規確認を省略する設定の混在選択では新規対象だけを承認し、拒否された既存導入先の対象を承認へ混ぜない。</summary>
    [TestMethod]
    public async Task ForceInstallPackagesAsync_DisabledNewConfirmationStillRejectsConfiguredDestinationInMixedSelection()
    {
        var events = new List<string>();
        var newPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(CreateChart())]);
        var existingPackage = ChartPackage.FromChartEntries([
            PackageChartEntry.FromChart(CreateChart(@"C:\Other\chart.bms", @"C:\Installed"))]);
        var store = new RecordingStore(events);
        var dialogs = new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.No)
        };
        PendingPackageWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, dialogs,
            settingsProvider: () => new InstallDestinationWorkflowSettingsSnapshot(false, false, false));

        PendingPackageMutationResult result = await owner.ForceInstallPackagesAsync([newPackage, existingPackage]);

        Assert.IsTrue(result.Succeeded);
        CollectionAssert.AreEquivalent(new[] { newPackage }, store.ApprovedNormalInstallOverridePackages.ToArray());
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Confirm_NormalInstallOverride, dialogs.ConfirmationRequest!.MessageBoxText);
    }

    /// <summary>手動導入の確認を取消すると変更境界を開始せず、失敗のない未実行結果を返して一覧反映を要求しない。</summary>
    [TestMethod]
    public async Task ManualInstallPackagesAsync_RejectionPreservesShellSelectionAndSkipsMutation()
    {

        var events = new List<string>();
        var store = new RecordingStore(events);
        var dialogs = new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
        };
        PendingPackageWorkflowOwner owner = CreateOwner(
            CreateLibrary,
            events,
            store,
            dialogs,
            playback: new NoOpChartMutationPlaybackPort(),
            settingsProvider: () => new InstallDestinationWorkflowSettingsSnapshot(
                showManualInstallConfirmation: true,
                deletePendingPackageSourceAfterInstall: true));
        PendingPackageMutationResult result = await owner.ManualInstallPackagesAsync(
            [ChartPackage.FromChartEntries([PackageChartEntry.FromChart(CreateChart())])]);

        Assert.IsFalse(result.Succeeded);
        Assert.IsNull(result.Failure);
        Assert.IsFalse(result.ShouldApplyView);
        Assert.AreEqual(0, events.Count);
        Assert.AreEqual(
            BeMusicSeeker.Properties.Resources.Msg_manual_installation_delete_source,
            dialogs.ConfirmationRequest!.MessageBoxText);
    }

    /// <summary>手動導入の確認表示が失敗すると元例外を結果へ保持し、変更境界と一覧反映を開始しない。</summary>
    [TestMethod]
    public async Task ManualInstallPackagesAsync_ConfirmationFailureReturnsBeforeMutation()
    {

        var events = new List<string>();
        var failure = new InvalidOperationException("manual confirmation failed");
        var store = new RecordingStore(events);
        var dialogs = new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.Failed(failure)
        };
        PendingPackageWorkflowOwner owner = CreateOwner(
            CreateLibrary,
            events,
            store,
            dialogs,
            playback: new NoOpChartMutationPlaybackPort(),
            settingsProvider: () => new InstallDestinationWorkflowSettingsSnapshot(
                showManualInstallConfirmation: true,
                deletePendingPackageSourceAfterInstall: true));

        PendingPackageMutationResult result = await owner.ManualInstallPackagesAsync(
            [ChartPackage.FromChartEntries([PackageChartEntry.FromChart(CreateChart())])]);

        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(result.Failure);
        Assert.AreSame(failure, result.Failure.InnerException);
        Assert.IsFalse(result.ShouldApplyView);
        Assert.AreEqual(0, events.Count);
    }

    /// <summary>強制導入と抑制終了がともに失敗しても一覧終端の反映を許可し、元の変更失敗と後片付け失敗を順に保持する。</summary>
    [TestMethod]
    public async Task ForceInstallPackagesAsync_MutationFailurePreservesTerminalViewApply()
    {
        var events = new List<string>();
        var mutationFailure = new InvalidOperationException("force install failed");
        var cleanupFailure = new InvalidOperationException("refresh cleanup failed");
        var store = new RecordingStore(events)
        {
            Failure = mutationFailure,
            EmptyPendingSection = true
        };
        var presentation = new RecordingPresentation(events)
        {
            EndRefreshSuppressionFailure = cleanupFailure
        };
        PendingPackageWorkflowOwner owner = CreateOwner(
            CreateLibrary,
            events,
            store,
            AcceptedDialogs(),
            presentation: presentation,
            playback: new RecordingPlayback(events));

        PendingPackageMutationResult result = await owner.ForceInstallPackagesAsync(
            [ChartPackage.FromChartEntries([PackageChartEntry.FromChart(CreateChart())])]);

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.ShouldApplyView);
        Assert.AreEqual(PackageCatalogSection.Pending, result.EmptySection);
        Assert.IsInstanceOfType<AggregateException>(result.Failure);
        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "playback-stop",
                "suppression-start",
                "store-force-install",
                "suppression-end",
                "activity-end"
            },
            events);
        var exception = (AggregateException)result.Failure;
        Assert.AreSame(mutationFailure, exception.InnerExceptions[0]);
        Assert.AreSame(cleanupFailure, exception.InnerExceptions[1]);
    }

    /// <summary>変更開始前の抑制失敗では空区画を照会せず、元失敗を保持して未捕捉の区画情報を捏造しない。</summary>
    [TestMethod]
    public async Task ForceInstallPackagesAsync_DoesNotCapturePendingSectionBeforeMutation()
    {
        var events = new List<string>();
        var suppressionFailure = new InvalidOperationException("suppression start failed");
        var store = new RecordingStore(events)
        {
            EmptyPendingSection = true,
            EmptyPendingSectionFailure = new InvalidOperationException("empty-section query should not run")
        };
        var presentation = new RecordingPresentation(events)
        {
            StartRefreshSuppressionFailure = suppressionFailure
        };
        PendingPackageWorkflowOwner owner = CreateOwner(
            CreateLibrary,
            events,
            store,
            AcceptedDialogs(),
            presentation: presentation,
            playback: new RecordingPlayback(events));

        PendingPackageMutationResult result = await owner.ForceInstallPackagesAsync(
            [ChartPackage.FromChartEntries([PackageChartEntry.FromChart(CreateChart())])]);

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.ShouldApplyView);
        Assert.IsNull(result.EmptySection);
        Assert.AreEqual(0, store.IsPendingSectionEmptyCount);
        Assert.AreSame(suppressionFailure, result.Failure);
    }

    /// <summary>選択要求からのパッケージ解決が失敗すると、対象解決だけで止めて変更境界や一覧反映を開始しない。</summary>
    [TestMethod]
    public async Task InstallPendingAsync_ResolutionFailureDoesNotApplySelection()
    {
        var events = new List<string>();
        var failure = new InvalidOperationException("pending resolution failed");
        var store = new RecordingStore(events) { Failure = failure };
        PendingPackageWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, AcceptedDialogs());
        var request =
            PendingInstallPackageOperationRequest.CreateForceInstall([CreateTarget(CreateChart())]);

        PendingPackageMutationResult result = await owner.InstallPendingAsync(request);

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(result.ShouldApplyView);
        Assert.AreSame(failure, result.Failure);
        CollectionAssert.AreEqual(new[] { "store-resolve-packages" }, events);
    }

    /// <summary>選択要求をstoreで解決した後に再生停止と変更境界を実行し、解決したパッケージだけを強制導入へ渡す。</summary>
    [TestMethod]
    public async Task InstallPendingAsync_ResolvesRequestBeforePreparingShellAndForceInstalling()
    {
        var events = new List<string>();
        ChartFile chart = CreateChart();
        var package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(chart)]);
        var store = new RecordingStore(events) { ResolvedPackages = [package] };
        PendingPackageWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, new FakeUiDialogService());
        var request =
            PendingInstallPackageOperationRequest.CreateForceInstall([CreateTarget(chart)]);

        PendingPackageMutationResult result = await owner.InstallPendingAsync(request);

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(result.Failure);
        Assert.IsTrue(result.ShouldApplyView);
        CollectionAssert.AreEqual(
            new[]
            {
                "store-resolve-packages",
                "activity-start",
                "playback-stop",
                "suppression-start",
                "store-force-install",
                "suppression-end",
                "activity-end"
            },
            events);
        Assert.AreSame(package, store.LastPackages.Single());
    }

    /// <summary>修復対象に導入先がない場合、専用案内だけを表示しstoreや変更境界を開始しない。</summary>
    [TestMethod]
    public async Task FixInstalledLocationsAsync_WarnsWithoutDestinationAndDoesNotMutate()
    {

        var events = new List<string>();
        var store = new RecordingStore(events);
        var dialogs = new FakeUiDialogService();
        PendingPackageWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, dialogs);
        Assert.IsTrue(RepairInstalledLocationRequest.TryCreate(
            [CreateTarget(CreateChart(), ChartOperationCapabilities.RepairInstalledLocation)],
            out RepairInstalledLocationRequest request));

        await owner.FixInstalledLocationsAsync(request);

        Assert.AreEqual(0, events.Count);
        Assert.AreEqual(
            BeMusicSeeker.Properties.Resources.Msg_fix_installation_warning,
            dialogs.MessageRequest!.MessageBoxText);
    }

    /// <summary>修復の事前警告表示が失敗した場合、元例外と修復案内の失敗分類を伝え、storeや変更境界を開始しない。</summary>
    [TestMethod]
    public async Task FixInstalledLocationsAsync_WarningDisplayFailurePropagates()
    {

        var events = new List<string>();
        var failure = new InvalidOperationException("warning display failed");
        var store = new RecordingStore(events);
        var dialogs = new FakeUiDialogService
        {
            MessageResult = UiDialogResult.Failed(failure)
        };
        PendingPackageWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, dialogs);
        Assert.IsTrue(RepairInstalledLocationRequest.TryCreate(
            [CreateTarget(CreateChart(), ChartOperationCapabilities.RepairInstalledLocation)],
            out RepairInstalledLocationRequest request));

        InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => owner.FixInstalledLocationsAsync(request));

        Assert.AreSame(failure, exception.InnerException);
        StringAssert.Contains(exception.Message, "Installed-location repair warning");
        Assert.AreEqual(0, events.Count);
    }

    /// <summary>修復と重複削除を順に承認すると、再生停止後にstoreへ承認済み譜面パスを渡しパッケージ変更の境界で処理する。</summary>
    [TestMethod]
    public async Task FixInstalledLocationsAsync_ApprovesDuplicateRemovalAndMutatesPackageScope()
    {

        var events = new List<string>();
        ChartFile chart = CreateChart(
            path: @"C:\Charts\repair.bms",
            installDestination: @"C:\Installed\repair.bms");
        var store = new RecordingStore(events)
        {
            DuplicateConfirmations =
            [
                new BMSLibrary.DuplicateInstallRepairConfirmation(
                    chart,
                    [@"C:\Duplicate\repair.bms"])
            ]
        };
        var presentation = new RecordingPresentation(events);
        var playback = new RecordingPlayback(events);
        var dialogs = new FakeUiDialogService();
        dialogs.EnqueueConfirmation(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        dialogs.EnqueueConfirmation(UiDialogResult.FromMessageBoxResult(MessageBoxResult.Yes));
        PendingPackageWorkflowOwner owner = CreateOwner(
            CreateLibrary,
            events,
            store,
            dialogs,
            presentation: presentation,
            playback: playback);
        Assert.IsTrue(RepairInstalledLocationRequest.TryCreate(
            [CreateTarget(chart, ChartOperationCapabilities.RepairInstalledLocation)],
            out RepairInstalledLocationRequest request));

        await owner.FixInstalledLocationsAsync(request);

        CollectionAssert.AreEqual(
            new[]
            {
                "store-get-duplicate-confirmations",
                "activity-start",
                "playback-stop",
                "suppression-start",
                "store-fix-installed-locations",
                "suppression-end",
                "activity-end"
            },
            events);
        Assert.AreEqual(PendingPackageRefreshScope.PackageMutation, presentation.LastRefreshScope);
        CollectionAssert.AreEqual(
            new[] { chart.Path },
            store.ApprovedDuplicateRemovalChartPaths.ToArray());
    }

    /// <summary>所持済み保留元の削除を承認すると、取得したパッケージを再生停止後の変更境界内でstoreへ渡す。</summary>
    [TestMethod]
    public async Task DeleteInstalledOnlyPendingPackageSourcesAsync_ApprovedMutationUsesOwnerBoundary()
    {

        var events = new List<string>();
        var package = ChartPackage.FromChartEntries([
            PackageChartEntry.FromChart(CreateChart())
        ]);
        var store = new RecordingStore(events) { InstalledOnlyPendingPackages = [package] };
        PendingPackageWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, AcceptedDialogs());

        await owner.DeleteInstalledOnlyPendingPackageSourcesAsync();

        CollectionAssert.AreEqual(
            new[]
            {
                "store-get-installed-only",
                "activity-start",
                "playback-stop",
                "suppression-start",
                "store-delete-sources",
                "suppression-end",
                "activity-end"
            },
            events);
        Assert.AreSame(package, store.LastPackages.Single());
    }

    /// <summary>所持済み保留元の対象が空なら、対象照会後に専用案内だけを表示して削除境界を開始しない。</summary>
    [TestMethod]
    public async Task DeleteInstalledOnlyPendingPackageSourcesAsync_EmptySelectionWarnsWithoutMutation()
    {

        var events = new List<string>();
        var store = new RecordingStore(events);
        FakeUiDialogService dialogs = AcceptedDialogs();
        PendingPackageWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, dialogs);

        await owner.DeleteInstalledOnlyPendingPackageSourcesAsync();

        CollectionAssert.AreEqual(new[] { "store-get-installed-only" }, events);
        Assert.AreEqual(
            BeMusicSeeker.Properties.Resources.Warn_no_pending_installed_only_packages,
            dialogs.MessageRequest!.MessageBoxText);
    }

    /// <summary>所持済み保留元の削除確認を取消すると、対象照会だけで止めて再生停止や削除storeを呼ばない。</summary>
    [TestMethod]
    public async Task DeleteInstalledOnlyPendingPackageSourcesAsync_RejectionSkipsMutation()
    {

        var events = new List<string>();
        var package = ChartPackage.FromChartEntries([
            PackageChartEntry.FromChart(CreateChart())
        ]);
        var store = new RecordingStore(events) { InstalledOnlyPendingPackages = [package] };
        var dialogs = new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
        };
        PendingPackageWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, dialogs);

        await owner.DeleteInstalledOnlyPendingPackageSourcesAsync();

        CollectionAssert.AreEqual(new[] { "store-get-installed-only" }, events);
    }

    /// <summary>ゼロノート保留の対象をstoreから取得し、再生停止を呼び出した後に抑制付きの変更境界で無効化処理へ渡す順序を保つ。</summary>
    [TestMethod]
    public async Task RenamePendingZeroNoteChartsAsync_StopsPlaybackBeforeMutation()
    {

        var events = new List<string>();
        ChartFile chart = CreateChart();
        var store = new RecordingStore(events) { PendingBmsFormatCharts = [chart] };
        var presentation = new RecordingPresentation(events);
        var playback = new RecordingPlayback(events);
        PendingPackageWorkflowOwner owner = CreateOwner(
            CreateLibrary,
            events,
            store,
            AcceptedDialogs(),
            presentation: presentation,
            playback: playback);

        await owner.RenamePendingZeroNoteChartsAsync();

        CollectionAssert.AreEqual(
            new[]
            {
                "store-get-pending-charts",
                "activity-start",
                "playback-stop",
                "suppression-start",
                "store-rename-zero-note",
                "suppression-end",
                "activity-end"
            },
            events);
    }

    /// <summary>正常な保留リソース上書きでは再生停止・抑制・store・終端の順を保ち、要求・処理・成功一件と失敗ゼロ・未取消の集計全文を案内する。</summary>
    [TestMethod]
    public async Task OverwriteInstalledOnlyPendingPackageResourcesAsync_ShowsSummaryAfterMutation()
    {

        var events = new List<string>();
        ChartFile chart = CreateChart();
        var package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(chart)]);
        var overwriteResult = new PendingInstalledOnlyResourceOverwriteResult
        {
            Requested = 1,
            Processed = 1,
            SucceededInstall = 1
        };
        var store = new RecordingStore(events)
        {
            InstalledOnlyPendingPackages = [package],
            OverwriteResult = overwriteResult
        };
        FakeUiDialogService dialogs = AcceptedDialogs();
        var presentation = new RecordingPresentation(events);
        var playback = new RecordingPlayback(events);
        PendingPackageWorkflowOwner owner = CreateOwner(
            CreateLibrary,
            events,
            store,
            dialogs,
            presentation: presentation,
            playback: playback);

        await owner.OverwriteInstalledOnlyPendingPackageResourcesAsync();

        CollectionAssert.AreEqual(
            new[]
            {
                "store-get-installed-only",
                "activity-start",
                "playback-stop",
                "suppression-start",
                "store-overwrite-resources",
                "suppression-end",
                "activity-end"
            },
            events);
        Assert.AreEqual(
            string.Format(BeMusicSeeker.Properties.Resources.Warn_overwrite_pending_installed_only_packages_summary,
                1, 1, 1, 0, 0, 0, 0, 0, 0, false),
            dialogs.MessageRequest!.MessageBoxText);
    }

    /// <summary>リソース上書きの反映・最終化・物理・回収・型衝突の異常を通常集計で隠さず、受付解放後に対象と原因を一度報告する。</summary>
    // failureKindは反映・最終化・物理・回収・型衝突。reporterThrowsは任意の報告表示失敗でも元の終端を変えない条件。
    [TestMethod]
    [DataRow("apply", false)]
    [DataRow("finalization", false)]
    [DataRow("physical", false)]
    [DataRow("cleanup", false)]
    [DataRow("conflict", false)]
    [DataRow("apply", true)]
    public async Task OverwriteInstalledOnlyPendingPackageResourcesAsync_ReportsSessionAfterRelease(
        string failureKind,
        bool reporterThrows)
    {

        var events = new List<string>();
        var failure = new IOException("resource-overwrite-terminal-" + failureKind);
        var target = new LibraryMutationSessionTarget(@"C:\pending", @"D:\installed");
        var conflict = new FileDbMutationDestinationTypeConflict(
            @"C:\pending\BGA", @"D:\installed\BGA",
            expectedIsDirectory: false, existingIsDirectory: true);
        bool durable = failureKind is "finalization" or "cleanup";
        bool conflictOnly = failureKind == "conflict";
        var sessionReceipt = new LibraryMutationSessionReceipt(
            confirmedTargets: failureKind is "physical" or "conflict" ? [] : [target],
            durableCommit: durable,
            physicalFailure: failureKind == "physical" ? failure : null,
            failedTarget: failureKind == "physical" ? target : null,
            applyFailure: failureKind == "apply" ? failure : null,
            finalizationFailure: failureKind == "finalization" ? failure : null,
            cleanupFailure: failureKind == "cleanup" ? failure : null,
            itemFailures: conflictOnly
                ? [new LibraryMutationSessionItemFailure(
                    target, new FileDbMutationDestinationTypeConflictException(conflict), [conflict])]
                : [],
            destinationTypeConflicts: conflictOnly ? [conflict] : []);
        var package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(CreateChart())]);
        var store = new RecordingStore(events)
        {
            InstalledOnlyPendingPackages = [package],
            OverwriteResult = new PendingInstalledOnlyResourceOverwriteResult
            {
                Requested = 1,
                Processed = 1,
                SucceededInstall = failureKind == "cleanup" ? 1 : 0,
                Failed = failureKind is "physical" or "conflict" ? 1 : 0,
                SessionReceipt = sessionReceipt
            }
        };
        var gate = new ChartFileOperationSynchronizer();
        bool releasedAtReport = false;
        var dialogs = new FileDbReportRecordingDialogs
        {
            MessageFailure = reporterThrows ? new IOException("optional-report-marker") : null,
            OnMessage = () =>
            {
                bool acquired = gate.TryEnter(out IDisposable lease);
                releasedAtReport = acquired && events.Contains("activity-end");
                lease?.Dispose();
            }
        };
        PendingPackageWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, dialogs, chartFileOperations: gate);

        await owner.OverwriteInstalledOnlyPendingPackageResourcesAsync();

        Assert.IsTrue(releasedAtReport);
        Assert.AreEqual(1, events.Count(value => value == "store-overwrite-resources"));
        Assert.AreEqual(1, dialogs.Messages.Count);
        Assert.AreEqual(
            failureKind is "cleanup" or "conflict" ? MessageBoxImage.Warning : MessageBoxImage.Error,
            dialogs.Messages[0].Icon);
        StringAssert.Contains(dialogs.Messages[0].MessageBoxText, target.SourcePath);
        if (conflictOnly)
        {
            StringAssert.Contains(dialogs.Messages[0].MessageBoxText, conflict.DestinationPath);
        }
        else
        {
            StringAssert.Contains(dialogs.Messages[0].MessageBoxText, failure.Message);
        }
    }

    /// <summary>複数の所持済み保留元を削除すると、削除の進捗案内へ渡し全対象をstoreへ接続する。</summary>
    [TestMethod]
    public async Task DeleteInstalledOnlyPendingPackageSourcesAsync_MultiplePackagesUsesProgressRoute()
    {

        var events = new List<string>();
        var first = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(CreateChart(@"C:\Charts\first.bms"))]);
        var second = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(CreateChart(@"C:\Charts\second.bms"))]);
        var store = new RecordingStore(events) { InstalledOnlyPendingPackages = [first, second] };
        FakeUiDialogService dialogs = AcceptedDialogs();
        PendingPackageWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, dialogs);

        await owner.DeleteInstalledOnlyPendingPackageSourcesAsync();

        Assert.IsNotNull(dialogs.ProgressRequest);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Remove, dialogs.ProgressRequest.Title);
        CollectionAssert.AreEqual(new[] { first, second }, (System.Collections.ICollection)store.LastPackages);
    }

    /// <summary>複数保留元の削除に伴う進捗案内が失敗すると、元表示例外を内部例外に保持して呼出し元へ伝える。</summary>
    [TestMethod]
    public async Task DeleteInstalledOnlyPendingPackageSourcesAsync_ProgressFailurePropagates()
    {

        var events = new List<string>();
        var first = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(CreateChart(@"C:\Charts\first.bms"))]);
        var second = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(CreateChart(@"C:\Charts\second.bms"))]);
        var store = new RecordingStore(events) { InstalledOnlyPendingPackages = [first, second] };
        var failure = new InvalidOperationException("progress failed");
        FakeUiDialogService dialogs = AcceptedDialogs();
        dialogs.ProgressResult = new UiProgressResult(UiDialogStatus.Failed, error: failure);
        PendingPackageWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, dialogs);

        InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            owner.DeleteInstalledOnlyPendingPackageSourcesAsync);

        Assert.AreSame(failure, exception.InnerException);
    }

    /// <summary>保留元削除の利用者取消を取消待機する代替storeへ伝え、処理Taskの終端を例外にせず正常に返す。</summary>
    [TestMethod]
    public async Task DeleteInstalledOnlyPendingPackageSourcesAsync_UserCancellationIsNormalCompletion()
    {

        var events = new List<string>();
        var first = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(CreateChart(@"C:\Charts\first.bms"))]);
        var second = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(CreateChart(@"C:\Charts\second.bms"))]);
        var store = new RecordingStore(events)
        {
            InstalledOnlyPendingPackages = [first, second],
            WaitForDeleteSourcesCancellation = true
        };
        FakeUiDialogService dialogs = AcceptedDialogs();
        dialogs.ProgressResult = new UiProgressResult(UiDialogStatus.CancelledByUser);
        PendingPackageWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, dialogs);

        await owner.DeleteInstalledOnlyPendingPackageSourcesAsync();

        CollectionAssert.Contains(events, "store-delete-sources");
    }

    /// <summary>進捗表示と削除処理がともに失敗すると、元表示例外と元削除例外を順序付きの集約で保持する。</summary>
    [TestMethod]
    public async Task DeleteInstalledOnlyPendingPackageSourcesAsync_PreservesDialogAndMutationFailures()
    {

        var events = new List<string>();
        var first = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(CreateChart(@"C:\Charts\first.bms"))]);
        var second = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(CreateChart(@"C:\Charts\second.bms"))]);
        var mutationFailure = new IOException("source deletion failed");
        var store = new RecordingStore(events)
        {
            InstalledOnlyPendingPackages = [first, second],
            DeleteSourcesFailure = mutationFailure
        };
        var dialogFailure = new InvalidOperationException("progress failed");
        FakeUiDialogService dialogs = AcceptedDialogs();
        dialogs.ProgressResult = new UiProgressResult(UiDialogStatus.Failed, error: dialogFailure);
        PendingPackageWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, dialogs);

        AggregateException exception = await Assert.ThrowsExactlyAsync<AggregateException>(
            owner.DeleteInstalledOnlyPendingPackageSourcesAsync);

        Assert.AreSame(dialogFailure, exception.InnerExceptions[0].InnerException);
        Assert.AreSame(mutationFailure, exception.InnerExceptions[1]);
    }

    /// <summary>手動・強制導入の抑制終了が失敗しても、確定済みの元receiptと最終化失敗を結果へ保持して受付を解放する。</summary>
    // falseは強制導入、trueは手動導入。同じ確定receiptを抑制終了の失敗後も保持する。
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InstallRetainsReceiptWhenOuterSuppressionCleanupFails(bool manual)
    {
        var events = new List<string>();
        var primary = new IOException("pending-primary-marker");
        var cleanup = new IOException("pending-cleanup-marker");
        var sessionReceipt = new LibraryMutationSessionReceipt(
            [new LibraryMutationSessionTarget(@"C:\source", @"D:\destination")],
            durableCommit: true,
            finalizationFailure: primary,
            cleanupFailure: cleanup);
        var store = new RecordingStore(events) { TerminalReceipt = sessionReceipt };
        var gate = new ChartFileOperationSynchronizer();
        PendingPackageWorkflowOwner owner = CreateOwner(CreateLibrary, events, store, AcceptedDialogs(), chartFileOperations: gate);
        var scopeFailure = new IOException("pending-scope-marker");
        owner.WorkflowChanged += (_, args) =>
        {
            if (args is PendingPackageRefreshSuppressionChangedEventArgs suppression && !suppression.IsSuppressed)
            {
                throw scopeFailure;
            }
        };
        var package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(CreateChart())]);
        PendingPackageMutationResult result = manual
            ? await owner.ManualInstallPackagesAsync([package]) : await owner.ForceInstallPackagesAsync([package]);
        Assert.AreSame(sessionReceipt, result.SessionReceipt);
        Assert.AreSame(scopeFailure, result.Failure);
        Assert.IsTrue(result.HasDurableCommit);
        Assert.IsTrue(result.HasDurableFinalizationFailure);
        Assert.IsTrue(gate.TryEnter(out IDisposable released));
        released.Dispose();
        Assert.AreEqual(1, events.Count(value => value == (manual ? "store-manual-install" : "store-force-install")));
    }

    /// <summary>実ownerの型衝突結果を画面終端へ渡すと、変更受付とactivity解放後に衝突先を含む警告を一度表示する。</summary>
    [TestMethod]
    public async Task RealPendingOwnerResultReportsConflictAfterOwnerReleasesMutationGate()
    {

        var conflict = new FileDbMutationDestinationTypeConflict(
            @"C:\pending\BGA",
            @"D:\installed\BGA",
            expectedIsDirectory: false,
            existingIsDirectory: true);
        var conflictFailure = new FileDbMutationDestinationTypeConflictException(conflict);
        var sessionReceipt = new LibraryMutationSessionReceipt(
            [],
            durableCommit: false,
            itemFailures:
            [
                new LibraryMutationSessionItemFailure(
                    new LibraryMutationSessionTarget(@"C:\pending", @"D:\installed"),
                    conflictFailure,
                    [conflict])
            ],
            destinationTypeConflicts: [conflict]);
        var events = new List<string>();
        var store = new RecordingStore(events)
        {
            TerminalReceipt = sessionReceipt
        };
        var gate = new ChartFileOperationSynchronizer();
        bool releasedAtReport = false;
        var dialogs = new FileDbReportRecordingDialogs
        {
            OnMessage = () =>
            {
                bool acquired = gate.TryEnter(out IDisposable lease);
                releasedAtReport = acquired && events.Contains("activity-end");
                lease?.Dispose();
            }
        };
        PendingPackageWorkflowOwner owner = CreateOwner(
            CreateLibrary,
            events,
            store,
            dialogs,
            chartFileOperations: gate);
        var package = ChartPackage.FromChartEntries([
            PackageChartEntry.FromChart(CreateChart())
        ]);

        PendingPackageMutationResult result = await owner.ManualInstallPackagesAsync([package]);
        var terminal = new MainWindowPendingPackageMutationViewTerminal(
            () => false,
            () => 1,
            _ => Task.FromResult(true),
            dialogs);
        await terminal.ApplyAsync(
            result,
            PackageCatalogSection.Pending,
            MainViewUpdateMode.PendingInstallFolderSelected,
            nameof(RealPendingOwnerResultReportsConflictAfterOwnerReleasesMutationGate));

        Assert.IsTrue(releasedAtReport);
        Assert.AreEqual(1, dialogs.Messages.Count);
        Assert.AreEqual(System.Windows.MessageBoxImage.Warning, dialogs.Messages[0].Icon);
        StringAssert.Contains(dialogs.Messages[0].MessageBoxText, conflict.DestinationPath);
        Assert.AreSame(conflict, result.DestinationTypeConflicts[0]);
        Assert.AreEqual(1, events.Count(value => value == "store-manual-install"));
    }

    /// <summary>同じ推定先への実導入はAだけを一回保存し、Bの既存資源の復元不能でCを止める。Cの保留・元bytes・全譜面の導入先と未処理記録を保ち、後片付け・受付解放後に終了へ渡す。</summary>
    /// <remarks>実FS・SQLite・本番storeを動かし、最終プロセス終了だけを記録へ差し替える。通常失敗・補償成功の条件分岐は既存回帰と下位caseが分担する。</remarks>
    [TestMethod]
    public async Task ManualInstallPackagesAsync_UnrestoredDestinationCommitsPrefixAndExitsAfterOwnedTerminal()
    {
        using var caseWorkspace11 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(caseWorkspace11.RootDirectory, "PendingUnrestored_" + Guid.NewGuid().ToString("N"));
        Task<PendingPackageMutationResult>? operation = null;
        TestBmsLibrary? library = null;
        using var releaseCleanup = new ManualResetEventSlim();
        try
        {
            (TestBmsLibrary Library, ChartPackage[] Packages, UnrestoredDestinationFileMutationService Files, ChartFileOperationSynchronizer Playlist) workspace = CreateUnrestoredDestinationLibrary(root, installedOnly: false);
            library = workspace.Library;
            ChartPackage[] packages = workspace.Packages;
            string destination = Path.Combine(root, "installed");
            var sourceBytes = packages.Skip(1).SelectMany(package => Directory.GetFiles(package.path))
                .ToDictionary(path => path, File.ReadAllBytes);
            UnrestoredDestinationFileMutationService files = workspace.Files;
            ChartFileOperationSynchronizer playlist = workspace.Playlist;
            ChartFileOperationSynchronizer gate = library.OperationAdmission;
            var activity = new ChartMutationActivityOwner();
            var cleanupReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            activity.ActivityChanged += (_, _) => { if (!activity.IsActive) { cleanupReached.TrySetResult(); releaseCleanup.Wait(); } };
            int exits = 0;
            bool releasedAtExit = false;
            Exception? exitFailure = null;
            FakeUiDialogService dialogs = AcceptedDialogs();
            var owner = new PendingPackageWorkflowOwner(() => library, gate, activity,
                new NoOpChartMutationPlaybackPort(), dialogs, DefaultSettings,
                new TestExternalShellGateway(_ => new ExplorerOpenResult(), _ => new ExplorerOpenResult()),
                new BmsLibraryPendingPackageStore(), failure =>
                {
                    exits++;
                    exitFailure = failure;
                    releasedAtExit = !gate.IsActive && !playlist.IsActive && !activity.IsActive;
                    return Task.CompletedTask;
                });
            operation = owner.ManualInstallPackagesAsync(packages);
            Assert.AreSame(cleanupReached.Task, await Task.WhenAny(cleanupReached.Task, operation));
            Assert.AreEqual(0, exits);
            Assert.IsFalse(operation.IsCompleted);
            Assert.IsTrue(gate.IsActive);
            Assert.IsFalse(playlist.IsActive, "通常モードの変更にはP取得が不要です。");
            releaseCleanup.Set();
            PendingPackageMutationResult result = await operation;
            Assert.IsNotNull(result.SessionReceipt);
            LibraryMutationSessionReceipt receipt = result.SessionReceipt;
            Assert.IsTrue(receipt.HasUnrestoredExistingDestination);
            Assert.IsTrue(receipt.WithFinalizationFailure(new IOException("later finalization")).HasUnrestoredExistingDestination);
            Assert.IsTrue(receipt.DurableCommit);
            CollectionAssert.AreEquivalent(new[] { Path.Combine(destination, "chart0.bms"), Path.Combine(destination, "first.wav") },
                receipt.ConfirmedTargets.Select(target => target.DestinationPath).ToArray());
            Assert.AreEqual(1, receipt.ApplyCounts.InstalledTargetApplyCount);
            Assert.AreEqual(1, receipt.UnprocessedTargets.Count);
            Assert.AreEqual(packages[2].path, receipt.UnprocessedTargets[0].SourcePath);
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(1, exits);
            Assert.IsTrue(releasedAtExit);
            Assert.AreSame(receipt.PrimaryFailure, exitFailure);
            Assert.AreEqual(2, files.FailedMoveCount, "公開失敗と一回だけの復元試行です。");
            Assert.IsNotNull(files.BackupPath);
            Assert.AreEqual("old resource", File.ReadAllText(files.BackupPath));
            foreach ((string path, byte[] bytes) in sourceBytes) { CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path)); }
            Assert.IsFalse(File.Exists(Path.Combine(destination, "chart2.bms")));
            Assert.AreEqual("new resource", File.ReadAllText(Path.Combine(destination, "first.wav")));
            Assert.IsTrue(library.BmsCharts.Any(chart => chart.Path == Path.Combine(destination, "chart0.bms")));
            Assert.IsFalse(library.BmsCharts.Any(chart => chart.Path == Path.Combine(destination, "chart1.bms")));
            CollectionAssert.AreEquivalent(packages.Skip(1).Select(package => package.path).ToArray(), library.ChartPackagesPending.Select(package => package.path).ToArray());
            foreach (ChartPackage pending in library.ChartPackagesPending)
            {
                foreach (PackageChartEntry entry in pending.ChartEntries) { Assert.AreEqual(destination, entry.Chart.InstallDestination); }
            }
            Assert.AreEqual(2, library.ChartPackagesPending.Single(package => package.path == packages[2].path).ChartEntries.Count);
            Assert.IsNotNull(receipt.PhysicalFailure);
            Assert.IsNotNull(receipt.FailedTarget);
            StringAssert.StartsWith(receipt.FailedTarget.SourcePath, packages[1].path);
            using LR2SongDBExtended db = new BmsLibraryDbGateway(library.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), Path.Combine(root, "song.db")).OpenSongDbReadOnly();
            Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", Path.Combine(destination, "chart0.bms")));
            Assert.AreEqual(0, db.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", Path.Combine(destination, "chart1.bms")));
            Assert.AreEqual(2, db.ExecuteScalar<int>("SELECT COUNT(1) FROM song;"));
            Assert.AreEqual(0, dialogs.MessageRequests.Count);
        }
        finally
        {
            releaseCleanup.Set();
            try { if (operation != null) { await operation; } }
            finally { library?.RequestShutdown("pending-unrestored-fixture"); if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
        }
    }

    /// <summary>installed-onlyの実資源上書きで復元不能が生じるとbulk実処理とL/P・activityの終端を待って終了へ渡し、通常集計へ戻らない。</summary>
    /// <remarks>別の戻り値とbulk経路を本番store・FS・SQLiteで接続する。故障条件の全組合せは下位executorが担当する。</remarks>
    [TestMethod]
    public async Task OverwriteInstalledOnlyPendingPackageResourcesAsync_UnrestoredDestinationExitsAfterBulkTerminal()
    {
        using var caseWorkspace12 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(caseWorkspace12.RootDirectory, "OverwriteUnrestored_" + Guid.NewGuid().ToString("N"));
        Task? operation = null;
        TestBmsLibrary? library = null;
        using var releaseCleanup = new ManualResetEventSlim();
        try
        {
            (TestBmsLibrary Library, ChartPackage[] Packages, UnrestoredDestinationFileMutationService Files, ChartFileOperationSynchronizer Playlist) workspace = CreateUnrestoredDestinationLibrary(root, installedOnly: true);
            library = workspace.Library;
            ChartPackage[] packages = workspace.Packages;
            UnrestoredDestinationFileMutationService files = workspace.Files;
            ChartFileOperationSynchronizer playlist = workspace.Playlist;
            ChartFileOperationSynchronizer gate = library.OperationAdmission;
            var activity = new ChartMutationActivityOwner();
            var cleanupReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            activity.ActivityChanged += (_, _) => { if (!activity.IsActive) { cleanupReached.TrySetResult(); releaseCleanup.Wait(); } };
            int exits = 0;
            bool releasedAtExit = false;
            FakeUiDialogService dialogs = AcceptedDialogs();
            var owner = new PendingPackageWorkflowOwner(() => library, gate, activity,
                new NoOpChartMutationPlaybackPort(), dialogs, DefaultSettings,
                new TestExternalShellGateway(_ => new ExplorerOpenResult(), _ => new ExplorerOpenResult()),
                new BmsLibraryPendingPackageStore(), _ =>
                {
                    exits++;
                    releasedAtExit = !gate.IsActive && !playlist.IsActive && !activity.IsActive;
                    return Task.CompletedTask;
                });
            operation = owner.OverwriteInstalledOnlyPendingPackageResourcesAsync();
            Assert.AreSame(cleanupReached.Task, await Task.WhenAny(cleanupReached.Task, operation));
            Assert.AreEqual(0, exits);
            Assert.IsFalse(operation.IsCompleted);
            Assert.IsTrue(gate.IsActive);
            Assert.IsTrue(playlist.IsActive);
            releaseCleanup.Set();
            await operation;
            Assert.IsNotNull(dialogs.ProgressRequest, "複数対象のbulk経路を通ります。");
            Assert.AreEqual(1, exits);
            Assert.IsTrue(releasedAtExit);
            Assert.AreEqual(2, files.FailedMoveCount);
            Assert.IsNotNull(files.BackupPath);
            Assert.AreEqual("old resource", File.ReadAllText(files.BackupPath));
            Assert.AreEqual("old resource", File.ReadAllText(Path.Combine(root, "installed1", "note.wav")), "後続の資源へ進みません。");
            Assert.AreEqual(2, library.ChartPackagesPending.Count);
            Assert.AreEqual(0, dialogs.MessageRequests.Count, "通常集計・通常失敗報告へ戻りません。");
        }
        finally
        {
            releaseCleanup.Set();
            try { if (operation != null) { await operation; } }
            finally { library?.RequestShutdown("pending-unrestored-fixture"); if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
        }
    }

    /// <summary>独立したDBと小さい実ファイルで、手動導入または所持済み資源上書きの本番入力を作ります。</summary>
    private static (TestBmsLibrary Library, ChartPackage[] Packages, UnrestoredDestinationFileMutationService Files,
        ChartFileOperationSynchronizer Playlist) CreateUnrestoredDestinationLibrary(string root, bool installedOnly)
    {
        var databaseScope = new ApplicationDatabaseScope();
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        using (LR2SongDBExtended initialize = databaseScope.OpenSongWrite(songDbPath)) { }
        var packages = new List<ChartPackage>();
        var installed = new List<ChartFile>();
        for (int index = 0; index < (installedOnly ? 2 : 3); index++)
        {
            string source = Path.Combine(root, "pending" + index);
            string destination = Path.Combine(root, installedOnly ? "installed" + index : "installed");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            string chart = Path.Combine(source, installedOnly ? "chart.bms" : "chart" + index + ".bms");
            string resource = !installedOnly && index == 0 ? "first.wav" : "note.wav";
            File.WriteAllText(chart, "#TITLE Pending " + index + "\r\n#ARTIST Test\r\n#BPM 120\r\n#WAV01 " + resource + "\r\n#00111:01\r\n");
            File.WriteAllText(Path.Combine(source, resource), "new resource");
            var entries = new List<PackageChartEntry>
            {
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                    BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chart)), destination)
            };
            if (!installedOnly && index == 2)
            {
                string anotherChart = Path.Combine(source, "chart2-another.bms");
                File.WriteAllText(anotherChart, "#TITLE Pending 2 Another\r\n#BPM 120\r\n#WAV01 note.wav\r\n#00111:01\r\n");
                entries.Add(ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                    BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(anotherChart)), destination));
            }
            packages.Add(ChartPackageTestExtensions.CreatePackage(entries));
            packages[^1].path = source;
            packages[^1].delete_parent = false;
            if (installedOnly || index == 1)
            {
                string existing = Path.Combine(destination, installedOnly ? "chart.bms" : "owned.bms");
                if (installedOnly) { File.Copy(chart, existing); }
                else { File.WriteAllText(existing, "#TITLE Existing\r\n#BPM 120\r\n#WAV01 note.wav\r\n#00111:01\r\n"); }
                installed.Add(BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(existing)));
                File.WriteAllText(Path.Combine(destination, "note.wav"), "old resource");
            }
        }
        var files = new UnrestoredDestinationFileMutationService(Path.Combine(root, installedOnly ? "installed0" : "installed", "note.wav"));
        var playlist = new ChartFileOperationSynchronizer();
        var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, files, new RecordingLibraryDialogService(),
            new TestUiScheduler(() => null), () => new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = installedOnly,
                LR2CustomFolderOutputBaseDir = root,
                BMSInstallDir = Path.Combine(root, "installed"),
                DeletePendingPackageSourceAfterInstall = false,
                EnableSmartComponentOverwrite = false,
                KeepSmartOverwriteProtectedFilesByRenaming = false
            }, playlistOperationAdmission: playlist)
        { BmsCharts = installed, BmsonCharts = [], ChartPackagesPending = packages, ChartPackagesInstalled = [] };
        new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).UpsertSongs(installed);
        return (library, packages.ToArray(), files, playlist);
    }

    private static BMSLibrary CreateLibrary()
    {
        return (BMSLibrary)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSLibrary));
    }

    private static ChartOperationTarget CreateLooseTarget(
        ChartFile chart,
        ChartOperationCapabilities capabilities = ChartOperationCapabilities.UpdateInstallDestination)
    {
        return new ChartOperationTarget(
            chart,
            null,
            ChartOperationSourceScope.PendingPackage,
            isOwned: false,
            isPending: true,
            isPlaylistMissing: false,
            capabilities);
    }

}
