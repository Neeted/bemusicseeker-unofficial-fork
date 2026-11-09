using System.IO;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.TestSupport.Components.PendingPackageStoreTestFixture;
using static BeMusicSeeker.TestSupport.Components.PendingPackageWorkflowOwnerTestSupport;

namespace BeMusicSeeker.Tests;

public sealed partial class PendingPackageWorkflowOwnerTests
{
    /// <summary>実ファイル移動後のDB trigger失敗をモデルから受け、同期モデル案内へ迂回せず受付解放後に一度だけ報告する。</summary>
    /// <remarks>実FS移動とSQLite triggerの反映失敗を本番モデルへ通し、モデルの報告抑制とowner解放後の配送を接続する。</remarks>
    [TestMethod]
    public async Task FixInstalledLocationsAsync_RealModelFailureReportsOnlyAfterGateRelease()
    {
        var databaseScope = new ApplicationDatabaseScope();

        using var caseWorkspace1 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempRootPath = Path.Combine(
            caseWorkspace1.RootDirectory,
            "BeMusicSeeker_PendingRepairTerminal_" + Guid.NewGuid().ToString("N"));
        string sourceDirectoryPath = Path.Combine(tempRootPath, "Broken");
        string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed");
        string sourceChartPath = Path.Combine(sourceDirectoryPath, "chart.bms");
        string destinationChartPath = Path.Combine(destinationDirectoryPath, "chart.bms");
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        Directory.CreateDirectory(sourceDirectoryPath);
        Directory.CreateDirectory(destinationDirectoryPath);
        File.WriteAllText(sourceChartPath, "#PLAYER 1\r\n#TITLE Pending repair terminal\r\n");
        try
        {
            ChartFile chartOwner = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(sourceChartPath));
            using (LR2SongDBExtended songDb = databaseScope.OpenSongWrite(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
                songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(chartOwner), typeof(LR2SongDB.song));
                string escapedDestinationPath = destinationChartPath.Replace("'", "''");
                songDb.Execute(
                    "CREATE TRIGGER pending_repair_path_failure BEFORE INSERT ON song WHEN NEW.path = '"
                    + escapedDestinationPath
                    + "' BEGIN SELECT RAISE(ABORT, 'pending-repair-path-fault'); END;");
            }

            var dialogs = new FileDbReportRecordingDialogs();
            var library = new TestBmsLibrary(databaseScope,
                songDbPath,
                null,
                null,
                new ResilientFileMutationService(),
                dialogs)
            {
                BmsCharts = [chartOwner]
            };
            var events = new List<string>();
            var store = new RecordingStore(events)
            {
                FixInstalledLocationsAction = (activeLibrary, charts, approvedPaths, capability) =>
                    activeLibrary.FixInstallationDirectoryCharts(charts, approvedPaths, new LibraryOperationNotices(), capability: capability)
            };
            ChartFileOperationSynchronizer gate = library.OperationAdmission;
            bool reportAfterGateRelease = false;
            dialogs.OnMessage = () =>
            {
                bool acquired = gate.TryEnter(out IDisposable lease);
                reportAfterGateRelease = acquired && events.Contains("activity-end");
                lease?.Dispose();
            };

            PendingPackageWorkflowOwner owner = CreateOwner(
                () => library,
                events,
                store,
                dialogs,
                chartFileOperations: gate);
            ChartFile repairChart = ChartFileProjection.WithPackageState(
                (chartOwner),
                destinationDirectoryPath,
                string.Empty,
                string.Empty,
                []);
            Assert.IsTrue(RepairInstalledLocationRequest.TryCreate(
                [CreateTarget(repairChart, ChartOperationCapabilities.RepairInstalledLocation)],
                out RepairInstalledLocationRequest request));

            await owner.FixInstalledLocationsAsync(request);

            Assert.IsTrue(reportAfterGateRelease);
            Assert.AreEqual(1, dialogs.Messages.Count);
            Assert.AreEqual(0, dialogs.ModelMessages);
            Assert.AreEqual(1, events.Count(value => value == "store-fix-installed-locations"));
            Assert.IsFalse(File.Exists(sourceChartPath));
            Assert.IsTrue(File.Exists(destinationChartPath));
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(LongPathFileSystem.ToExtendedPath(tempRootPath), recursive: true);
            }
        }
    }

    /// <summary>activity終了の表示購読者が失敗しても推定は成功し、抑制・受付を解放する。推定の経路にOKだけの案内の発生源はなく、窓口へは何も依頼しない。</summary>
    [TestMethod]
    public async Task SearchPackagesAsync_ActivityNotificationFailureKeepsSuccess()
    {
        var databaseScope = new ApplicationDatabaseScope();

        using var caseWorkspace2 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(caseWorkspace2.RootDirectory, nameof(PendingPackageWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var events = new List<string>();
            var dialogService = new RecordingLibraryDialogService();
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (LR2SongDBExtended _ = databaseScope.OpenSongWrite(songDbPath))
            {
            }
            var library = new TestBmsLibrary(databaseScope, songDbPath, null!, null, null!, dialogService);
            var store = new RecordingStore(events);
            var failure = new InvalidOperationException("activity end failed");
            var presentation = new RecordingPresentation(events) { EndActivityFailure = failure };
            var gate = new ChartFileOperationSynchronizer();
            var dialogs = new FileDbReportRecordingDialogs();
            PendingPackageWorkflowOwner owner = CreateOwner(
                () => library,
                events,
                store,
                dialogs,
                presentation: presentation,
                playback: new NoOpChartMutationPlaybackPort(),
                chartFileOperations: gate);

            await owner.SearchPackagesAsync(
                [new ChartPackage()]);

            Assert.IsTrue(gate.TryEnter(out IDisposable lease), "推定の終端の後に論理Lは解放されている。");
            lease.Dispose();
            Assert.AreEqual(0, dialogs.Messages.Count, "案内の発生源がない推定は、窓口へ何も依頼しない。");
            Assert.AreEqual(0, dialogService.CallCount + dialogService.PostCount, "モデルの窓口へ同期にも投函でも届けない。");
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
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    /// <summary>変更と必須の抑制終了の失敗を保持し、activity表示の任意失敗は追加しない。残る受付の解放を続け、窓口へは何も依頼しない。</summary>
    [TestMethod]
    public async Task SearchPackagesAsync_PreservesRequiredFailuresButDoesNotPromoteNotificationFailure()
    {
        var databaseScope = new ApplicationDatabaseScope();

        using var caseWorkspace3 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(caseWorkspace3.RootDirectory, nameof(PendingPackageWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var mutationFailure = new InvalidOperationException("mutation failed");
            var suppressionFailure = new InvalidOperationException("suppression end failed");
            var activityFailure = new InvalidOperationException("activity end failed");
            var events = new List<string>();
            var dialogService = new RecordingLibraryDialogService();
            var dialogs = new FileDbReportRecordingDialogs();
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (LR2SongDBExtended _ = databaseScope.OpenSongWrite(songDbPath))
            {
            }
            var library = new TestBmsLibrary(databaseScope, songDbPath, null!, null, null!, dialogService);
            var store = new RecordingStore(events)
            {
                Failure = mutationFailure
            };
            var presentation = new RecordingPresentation(events)
            {
                EndRefreshSuppressionFailure = suppressionFailure,
                EndActivityFailure = activityFailure
            };
            var gate = new ChartFileOperationSynchronizer();
            PendingPackageWorkflowOwner owner = CreateOwner(
                () => library,
                events,
                store,
                dialogs,
                presentation: presentation,
                playback: new NoOpChartMutationPlaybackPort(),
                chartFileOperations: gate);

            AggregateException exception = await Assert.ThrowsExactlyAsync<AggregateException>(
                () => owner.SearchPackagesAsync(
                    [new ChartPackage()]));

            CollectionAssert.AreEqual(
                new Exception[] { mutationFailure, suppressionFailure },
                exception.InnerExceptions);
            Assert.IsTrue(gate.TryEnter(out IDisposable lease), "必須失敗の後も論理Lを解放する。");
            lease.Dispose();
            Assert.AreEqual(0, dialogs.Messages.Count);
            Assert.AreEqual(0, dialogService.CallCount + dialogService.PostCount);
            CollectionAssert.AreEqual(
                new[]
                {
                    "activity-start",
                    "suppression-start",
                    "store-search-packages",
                    "suppression-end",
                    "activity-end"
                },
                events);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    /// <summary>
    /// 導入先の編集が拒否されたら、拒否の理由を所有者の窓口へ一件だけ、論理Lの解放後に、UIの文脈の外から依頼する。
    /// 表示の完了を保留しても編集のTaskは完了し、モデルの窓口へは同期にも投函でも届かず、保留の導入先は変わらない。
    /// モデルの書込み保護の内側での同期の表示、Lの保持中の依頼、表示の完了待ちを検出する。
    /// </summary>
    [TestMethod]
    public async Task SetPendingAsync_RejectedDestinationWarnsOnceAfterAdmissionRelease()
    {
        var databaseScope = new ApplicationDatabaseScope();

        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectory = Path.Combine(caseWorkspace.RootDirectory, nameof(PendingPackageWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        OperationNoticeRequestProbe? probe = null;
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (LR2SongDBExtended _ = databaseScope.OpenSongWrite(songDbPath))
            {
            }
            var modelDialogs = new RecordingLibraryDialogService();
            var library = new TestBmsLibrary(databaseScope, songDbPath, null!, null, null!, modelDialogs);
            string missingDestination = Path.Combine(tempDirectory, "missing-destination");
            ChartFile pendingFile = ChartTestValues.Empty() with { Path = Path.Combine(tempDirectory, "Pkg", "chart.bms") };
            PackageChartEntry entry = ChartPackageTestExtensions.CreateEntryWithInstallDestination(pendingFile, string.Empty);
            var package = ChartPackage.FromChartEntries([entry]);
            package.path = Path.Combine(tempDirectory, "Pkg");
            package.delete_parent = false;
            library.ChartPackagesPending = new System.Collections.ObjectModel.ObservableCollection<ChartPackage>([package]);
            ChartFileOperationSynchronizer gate = library.OperationAdmission;
            probe = new OperationNoticeRequestProbe(() => TestUiDispatcherHost.Dispatcher.CheckAccess(), () =>
            {
                if (!gate.TryEnter(out IDisposable lease)) { return false; }
                lease.Dispose();
                return true;
            });
            var events = new List<string>();
            PendingPackageWorkflowOwner owner = CreateOwner(() => library, events, new BmsLibraryPendingPackageStore(), probe,
                chartFileOperations: gate, playback: new NoOpChartMutationPlaybackPort());
            Assert.IsTrue(PendingInstallDestinationEditRequest.TryCreate(new ChartOperationTarget(entry.Chart, null,
                ChartOperationSourceScope.PendingPackage, isOwned: false, isPending: true, isPlaylistMissing: false,
                capabilities: ChartOperationCapabilities.UpdateInstallDestination, packageEntry: entry), out PendingInstallDestinationEditRequest request));

            Task edit = owner.SetPendingAsync(request, missingDestination);
            try { await ConcurrencyProbe.AssertCompletesWhileHeldAsync(edit, "pending-destination-rejection-display-held"); }
            finally { probe.ReleaseAll(); }
            await edit;

            OperationNoticeRequestProbe.Request notice = probe.Requests.Single();
            Assert.AreEqual(string.Format(BeMusicSeeker.Properties.Resources.Warn_InstallDirNotFound, missingDestination), notice.Message.MessageBoxText);
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.MessageBoxTitle_Warning, notice.Message.Caption);
            Assert.AreEqual(MessageBoxImage.Exclamation, notice.Message.Icon);
            Assert.AreEqual(MessageBoxButton.OK, notice.Message.Button);
            Assert.IsFalse(notice.OnUiContext, "UIの文脈では依頼しない。");
            Assert.IsTrue(notice.AdmissionsWereFree, "依頼の時点で論理Lは解放済み（モデルの書込み保護も解放済み）。");
            Assert.AreEqual(0, modelDialogs.CallCount + modelDialogs.PostCount, "モデルの窓口へ同期にも投函でも届けない。");
            Assert.IsTrue(string.IsNullOrEmpty(entry.Chart.InstallDestination), "拒否した編集は保留の導入先を変えない。");
        }
        finally
        {
            probe?.ReleaseAll();
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    /// <summary>推定導入先のない実保留を手動導入しても確定を作らず、入力bytesと保留entry・所持集合・空のsong行を保持する。</summary>
    /// <remarks>実解析した譜面と入力bytes、実SQLiteの空song行、本番保留storeで未実行の保全を確認する。</remarks>
    [TestMethod]
    public async Task ManualInstallPackagesAsync_NoEstimatedDestinationsCompletesWithEmptyReceiptAndPreservesPendingSources()
    {
        var databaseScope = new ApplicationDatabaseScope();

        using var caseWorkspace4 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(caseWorkspace4.RootDirectory, nameof(PendingPackageWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        string pendingPath = Path.Combine(root, "pending");
        Directory.CreateDirectory(pendingPath);
        try
        {
            string songDbPath = Path.Combine(root, "song.db");
            File.WriteAllBytes(songDbPath, []);
            using (LR2SongDBExtended initializeSongDb = databaseScope.OpenSongWrite(songDbPath))
            {
            }

            byte[] sourceContents = System.Text.Encoding.ASCII.GetBytes(
                "#TITLE Pending chart\r\n#ARTIST Test\r\n#BPM 120\r\n#PLAYLEVEL 1\r\n#WAV01 note.wav\r\n#00111:01\r\n");
            string chartPath = Path.Combine(pendingPath, "chart.bms");
            File.WriteAllBytes(chartPath, sourceContents);
            File.WriteAllBytes(Path.Combine(pendingPath, "note.wav"), [1, 2, 3]);
            ChartFile chart = (BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath)));
            var package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(chart)]);
            package.path = pendingPath;
            package.delete_parent = false;
            ChartFile pendingChart = package.ChartEntries.Single().Chart;
            string pendingChartPath = pendingChart.Path;
            var library = new TestBmsLibrary(databaseScope,
                songDbPath,
                null,
                null,
                new ResilientFileMutationService(),
                new RecordingLibraryDialogService(),
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false,
                    BMSInstallDir = Path.Combine(root, "installed"),
                    FolderNameFormat = "%TITLE%",
                    DeletePendingPackageSourceAfterInstall = false,
                    EnableSmartComponentOverwrite = false,
                    KeepSmartOverwriteProtectedFilesByRenaming = false
                })
            {
                BmsCharts = [],
                BmsonCharts = [],
                ChartPackagesPending = [package],
                ChartPackagesInstalled = []
            };
            var events = new List<string>();
            FakeUiDialogService dialogs = AcceptedDialogs();
            PendingPackageWorkflowOwner owner = CreateOwner(
                () => library,
                events,
                new BmsLibraryPendingPackageStore(),
                dialogs,
                chartFileOperations: library.OperationAdmission,
                playback: new NoOpChartMutationPlaybackPort(),
                settingsProvider: () => new InstallDestinationWorkflowSettingsSnapshot(
                    showManualInstallConfirmation: false,
                    deletePendingPackageSourceAfterInstall: false));

            Assert.AreEqual(1, package.ChartEntries.Count);
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingChart.InstallDestination));
            Assert.AreEqual(0, library.BmsCharts.Count);
            Assert.AreEqual(0, library.ChartPackagesInstalled.Count);
            using (LR2SongDBExtended beforeInstall = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(0, beforeInstall.ExecuteScalar<int>("SELECT COUNT(1) FROM song;"));
            }

            PendingPackageMutationResult result = await owner.ManualInstallPackagesAsync([package]);

            Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
            Assert.IsNull(result.Failure);
            Assert.AreSame(LibraryMutationSessionReceipt.Empty, result.SessionReceipt);
            Assert.IsFalse(result.HasDurableCommit);
            Assert.AreEqual(1, library.ChartPackagesPending.Count);
            Assert.AreSame(package, library.ChartPackagesPending.Single());
            ChartFile preservedChart = package.ChartEntries.Single().Chart;
            Assert.AreEqual(pendingChartPath, preservedChart.Path);
            Assert.IsTrue(string.IsNullOrWhiteSpace(preservedChart.InstallDestination));
            Assert.AreEqual(0, library.BmsCharts.Count);
            Assert.AreEqual(0, library.ChartPackagesInstalled.Count);
            Assert.IsTrue(File.Exists(chartPath));
            CollectionAssert.AreEqual(sourceContents, File.ReadAllBytes(chartPath));
            using (LR2SongDBExtended afterInstall = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(0, afterInstall.ExecuteScalar<int>("SELECT COUNT(1) FROM song;"));
            }
            Assert.AreEqual(0, dialogs.MessageRequests.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 起動走査を無効にした実ライブラリの受付拒否を例外にせず、空の確定結果と案内を返して画面終端を反映できる。
    /// 未収束の警告は、モデルから直接出ず、手動導入の外側の受付を解放した後に所有者の窓口へ一件だけ届く。
    /// モデルからの直接の投函・同期の表示（所有者の表示の迂回）と、外側の受付を保持したままの表示を検出する。
    /// </summary>
    [TestMethod]
    public async Task ManualInstallPackagesAsync_DisabledStartupScanKeepsAdmissionRejectionNonExceptional()
    {
        var databaseScope = new ApplicationDatabaseScope();

        using var caseWorkspace5 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(caseWorkspace5.RootDirectory, nameof(PendingPackageWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string songDbPath = Path.Combine(root, "song.db");
            File.WriteAllBytes(songDbPath, []);
            var libraryDialogs = new RecordingLibraryDialogService();
            var library = new TestBmsLibrary(databaseScope, songDbPath, null!, null, null!, libraryDialogs);
            library.ResetCatalogPathConvergence(CatalogPathConvergenceBlockReason.StartupFileScanDisabled);
            FakeUiDialogService dialogs = AcceptedDialogs();
            bool? admissionAcquirableAtRequest = null;
            dialogs.OnMessage = () =>
            {
                bool entered = library.OperationAdmission.TryEnter(out IDisposable probe);
                if (entered) { probe.Dispose(); }
                admissionAcquirableAtRequest ??= entered;
            };
            PendingPackageWorkflowOwner owner = CreateOwner(
                () => library,
                [],
                new BmsLibraryPendingPackageStore(),
                dialogs,
                chartFileOperations: library.OperationAdmission,
                playback: new NoOpChartMutationPlaybackPort());
            var package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(CreateChart())]);

            PendingPackageMutationResult result = await owner.ManualInstallPackagesAsync([package]);

            Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
            Assert.IsNull(result.Failure);
            Assert.IsTrue(result.ShouldApplyView);
            Assert.AreSame(LibraryMutationSessionReceipt.Empty, result.SessionReceipt);
            Assert.IsFalse(result.HasDurableCommit);
            Assert.AreEqual(1, dialogs.MessageRequests.Count);
            Assert.AreEqual(true, admissionAcquirableAtRequest, "表示の要求の時点で、手動導入の外側の受付を取得できる。");
            UiMessageRequest request = dialogs.MessageRequests[0];
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.Warn_CatalogFileMutationRequiresStartupScan, request.MessageBoxText);
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.MessageBoxTitle_Warning, request.Caption);
            Assert.AreEqual(MessageBoxImage.Exclamation, request.Icon);
            Assert.AreEqual(0, libraryDialogs.CallCount, "モデルの窓口へ同期の表示を要求しない。");
            Assert.AreEqual(0, libraryDialogs.PostCount, "モデルの窓口へ直接投函しない。");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 導入済みと同じ譜面を持つ保留の片付けだけの手動導入で、推定先導入の延期先から遅れて公開される完了の案内を落とさず、
    /// 外側の受付を解放した後に所有者の窓口へ一件だけ届け、モデルの窓口へは直接出さない。
    /// </summary>
    /// <remarks>収集先の取得を、延期先の公開より前に行うと、この案内が欠落して要求が0件になる。</remarks>
    [TestMethod]
    public async Task ManualInstallPackagesAsync_DeferredCleanupOnlyWarningIsShownOnceAfterAdmissionRelease()
    {
        var databaseScope = new ApplicationDatabaseScope();

        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(caseWorkspace.RootDirectory, nameof(PendingPackageWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string songDbPath = Path.Combine(root, "song.db");
            File.WriteAllBytes(songDbPath, []);
            string installedDirectory = Path.Combine(root, "installed");
            string source = Path.Combine(root, "pending");
            Directory.CreateDirectory(installedDirectory);
            Directory.CreateDirectory(source);
            const string text = "#TITLE AlreadyInstalled\n";
            File.WriteAllText(Path.Combine(installedDirectory, "chart.bms"), text);
            File.WriteAllText(Path.Combine(source, "chart.bms"), text);
            ChartFile installed = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(installedDirectory, "chart.bms")));
            ChartFile pendingChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(source, "chart.bms")));
            ChartPackage package = ChartPackageTestExtensions.CreatePackage(
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(pendingChart, installedDirectory));
            package.path = source;
            package.delete_parent = false;
            foreach (PackageChartEntry entry in package.ChartEntries) { BmsLibraryPackageInstallService.InitializePendingResourceHealth(entry); }
            var libraryDialogs = new RecordingLibraryDialogService();
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, new InstallServiceComponentTestSupport.RealFileMutationService(), libraryDialogs,
                new TestUiScheduler(() => null!),
                () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false,
                    BMSInstallDir = installedDirectory,
                    FolderNameFormat = "%TITLE%",
                    DeletePendingPackageSourceAfterInstall = true
                })
            {
                BmsCharts = [installed],
                ChartPackagesPending = new System.Collections.ObjectModel.ObservableCollection<ChartPackage>([package]),
                ChartPackagesInstalled = new System.Collections.ObjectModel.ObservableCollection<ChartPackage>()
            };
            FakeUiDialogService dialogs = AcceptedDialogs();
            bool? admissionAcquirableAtRequest = null;
            dialogs.OnMessage = () =>
            {
                bool entered = library.OperationAdmission.TryEnter(out IDisposable probe);
                if (entered) { probe.Dispose(); }
                admissionAcquirableAtRequest ??= entered;
            };
            PendingPackageWorkflowOwner owner = CreateOwner(
                () => library, [], new BmsLibraryPendingPackageStore(), dialogs,
                chartFileOperations: library.OperationAdmission,
                playback: new NoOpChartMutationPlaybackPort());

            PendingPackageMutationResult result = await owner.ManualInstallPackagesAsync([package]);

            Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
            Assert.AreEqual(1, dialogs.MessageRequests.Count);
            Assert.AreEqual(true, admissionAcquirableAtRequest, "表示の要求の時点で、外側の受付を取得できる。");
            UiMessageRequest request = dialogs.MessageRequests[0];
            Assert.AreEqual(string.Format(BeMusicSeeker.Properties.Resources.Warn_estimated_install_cleanup_only_completed, 1), request.MessageBoxText);
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.MessageBoxTitle_Warning, request.Caption);
            Assert.AreEqual(MessageBoxImage.Exclamation, request.Icon);
            Assert.AreEqual(0, libraryDialogs.CallCount);
            Assert.AreEqual(0, libraryDialogs.PostCount);
            Assert.IsFalse(Directory.Exists(source));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>共通受付のBusyを案内して推定storeを呼ばず、先行受付を解放しないまま拒否し、解放後の新しい要求だけを実行する。</summary>
    [TestMethod]
    public async Task SearchPackagesAsync_FailsFastWhenOwnerAdmissionIsBusyThenRunsAfterRelease()
    {
        var databaseScope = new ApplicationDatabaseScope();

        using var caseWorkspace6 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempRootPath = Path.Combine(
            caseWorkspace6.RootDirectory,
            "BeMusicSeeker_PendingWorkflowAdmissionTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            var library = new TestBmsLibrary(databaseScope, songDbPath);
            var events = new List<string>();
            var store = new RecordingStore(events);
            FakeUiDialogService dialogs = AcceptedDialogs();
            var gate = new ChartFileOperationSynchronizer();
            PendingPackageWorkflowOwner owner = CreateOwner(
                () => library,
                events,
                store,
                dialogs,
                playback: new NoOpChartMutationPlaybackPort(),
                chartFileOperations: gate);
            Assert.IsTrue(gate.TryEnter(out IDisposable incumbent));
            try
            {
                await owner.SearchPackagesAsync(
                    [new ChartPackage()]);
                Assert.AreEqual(0, store.SearchPackagesCount);
                Assert.IsFalse(gate.TryEnter(out IDisposable afterRejection));
                Assert.IsNull(afterRejection);
                Assert.AreEqual(
                    BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy,
                    dialogs.MessageRequest?.MessageBoxText);
            }
            finally
            {
                incumbent.Dispose();
            }

            await owner.SearchPackagesAsync(
                [new ChartPackage()]);
            Assert.AreEqual(1, store.SearchPackagesCount);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(LongPathFileSystem.ToExtendedPath(tempRootPath), recursive: true);
            }
        }
    }

    /// <summary>保留導入の確認待ちからdropを拒否し、成功・変更失敗・確認取消の実終端後に新しい要求だけを受理する。</summary>
    // manualで手動/強制入口、failMutationで変更失敗、cancelConfirmationで確認取消を分け、全終端でdrop受付を解放する。
    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, false, false)]
    [DataRow(true, true, false)]
    [DataRow(true, false, true)]
    public async Task PendingInstall_RejectsDropDuringConfirmationAndReleasesAdmission(
        bool manual, bool failMutation, bool cancelConfirmation)
    {
        var databaseScope = new ApplicationDatabaseScope();

        using var caseWorkspace7 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(caseWorkspace7.RootDirectory, nameof(PendingPackageWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        var confirmationEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmation = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        PackageInstallWorkflowOwner? automatic = null;
        Task<PendingPackageMutationResult>? pending = null;
        try
        {
            var library = new TestBmsLibrary(databaseScope, songDbPath);
            var gate = new ChartFileOperationSynchronizer();
            var events = new List<string>();
            var mutationFailure = new IOException("pending install failure marker");
            var store = new RecordingStore(events) { Failure = failMutation ? mutationFailure : null };
            ChartFile chart = CreateChart(installDestination: Path.Combine(root, "Installed"));
            var package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(chart)]);
            var dialogs = new FakeUiDialogService
            {
                ConfirmationHandler = _ =>
                {
                    confirmationEntered.TrySetResult(true);
                    return confirmation.Task;
                }
            };
            PendingPackageWorkflowOwner owner = CreateOwner(() => library, events, store, dialogs,
                chartFileOperations: gate,
                settingsProvider: () => new InstallDestinationWorkflowSettingsSnapshot(true, false));
            int autoCalls = 0;
            automatic = new PackageInstallWorkflowOwner(new FileDbReportRecordingDialogs(), gate, new ChartMutationActivityOwner(),
                new DelegatePackageInstallMutationPort((_, _, _, _) =>
                {
                    Interlocked.Increment(ref autoCalls);
                    return new PackageInstallCommandResult([], null);
                }),
            new NoOpChartMutationPlaybackPort(), action => { action(); return true; });
            automatic.AttachLibrary(library);

            pending = manual ? owner.ManualInstallPackagesAsync([package]) : owner.ForceInstallPackagesAsync([package]);
            await TestUiDispatcherHost.AwaitNotificationAsync(confirmationEntered.Task, pending, "pending-install.confirmation");
            Assert.IsFalse(automatic.TryEnqueue(new DroppedInstallBatchRequest([Path.Combine(root, "rejected.zip")])));
            Assert.IsTrue(automatic.IsIdle);
            Assert.AreEqual(0, autoCalls);
            Assert.AreEqual(0, events.Count, "確認完了前に store・再生・一覧へ副作用を出さない。");

            confirmation.SetResult(UiDialogResult.FromMessageBoxResult(cancelConfirmation
                ? MessageBoxResult.Cancel : manual ? MessageBoxResult.OK : MessageBoxResult.Yes));
            PendingPackageMutationResult result = await pending;
            Assert.AreEqual(!failMutation && !cancelConfirmation, result.Succeeded);
            Assert.AreSame(failMutation ? mutationFailure : null, result.Failure);
            if (cancelConfirmation)
            {
                Assert.IsFalse(result.ShouldApplyView);
                Assert.AreEqual(0, events.Count, "確認取消では実行や一覧更新を始めない。");
            }
            Assert.IsTrue(gate.TryEnter(out IDisposable chartAfterCompletion));
            chartAfterCompletion.Dispose();
            Assert.AreEqual(0, autoCalls, "拒否した drop を受付解放後に自動実行しない。");

            Assert.IsTrue(automatic.TryEnqueue(new DroppedInstallBatchRequest([Path.Combine(root, "fresh.zip")])));
            await automatic.WaitForIdleAsync();
            Assert.AreEqual(1, autoCalls);
        }
        finally
        {
            confirmation.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel));
            if (pending != null)
            {
                await pending;
            }

            if (automatic != null)
            {
                await automatic.WaitForIdleAsync();
            }

            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>一要求の複数入力を受理したworkerの開始前から終端まで保留導入と追加dropを拒否し、全入力処理と受付解放を観測した後に明示導入を受理する。</summary>
    // manualで手動/強制、selectedRowsで選択要求/パッケージ直接入口を分け、共通受付の拒否と再受付を確認する。
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task DropInstall_RejectsPendingInstallUntilAcceptedMultipleInputsFinish(bool manual, bool selectedRows)
    {
        var databaseScope = new ApplicationDatabaseScope();

        using var caseWorkspace8 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(caseWorkspace8.RootDirectory, nameof(PendingPackageWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        using var releaseEnqueue = new ManualResetEventSlim(false);
        var enqueueEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        PackageInstallWorkflowOwner? automatic = null;
        Task<bool>? enqueue = null;
        try
        {
            var library = new TestBmsLibrary(databaseScope, songDbPath);
            var gate = new ChartFileOperationSynchronizer();
            var events = new List<string>();
            ChartFile chart = CreateChart(installDestination: Path.Combine(root, "Installed"));
            var package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(chart)]);
            var store = new RecordingStore(events) { ResolvedPackages = [package] };
            var dialogs = new FakeUiDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(manual ? MessageBoxResult.OK : MessageBoxResult.Yes)
            };
            PendingPackageWorkflowOwner owner = CreateOwner(() => library, events, store, dialogs,
                chartFileOperations: gate,
                settingsProvider: () => new InstallDestinationWorkflowSettingsSnapshot(true, false));
            PendingInstallPackageOperationRequest request = manual
                ? PendingInstallPackageOperationRequest.CreateManualInstall([CreateTarget(chart)])
                : PendingInstallPackageOperationRequest.CreateForceInstall([CreateTarget(chart)]);
            Task<PendingPackageMutationResult> Install() => selectedRows
                ? owner.InstallPendingAsync(request)
                : manual ? owner.ManualInstallPackagesAsync([package]) : owner.ForceInstallPackagesAsync([package]);
            var paths = new List<string>();
            var rejectionsDuringBatch = new List<Task<PendingPackageMutationResult>>();
            var completionObservations = new List<(int ProcessedPathCount, bool AdmissionAvailable)>();
            int blockEnqueue = 0;
            automatic = new PackageInstallWorkflowOwner(new FileDbReportRecordingDialogs(), gate, new ChartMutationActivityOwner(),
                new DelegatePackageInstallMutationPort((_, batch, _, _) =>
                {
                    paths.AddRange(batch);
                    // 完了通知は受付解放後なので、競合拒否は実変更が戻る前に観測する。
                    rejectionsDuringBatch.Add(Install());
                    return new PackageInstallCommandResult([new ChartPackage()], null);
                }),
            new NoOpChartMutationPlaybackPort(), action =>
                {
                    if (Interlocked.Exchange(ref blockEnqueue, 0) == 1)
                    {
                        enqueueEntered.TrySetResult(true);
                        releaseEnqueue.Wait();
                    }
                    action();
                    return true;
                });
            automatic.AttachLibrary(library);
            automatic.CompletionPublished += _ =>
            {
                bool admissionAvailable = gate.TryEnter(out IDisposable completionLease);
                completionLease?.Dispose();
                completionObservations.Add((paths.Count, admissionAvailable));
            };
            Volatile.Write(ref blockEnqueue, 1);
            string firstPath = Path.Combine(root, "first.zip");
            string secondPath = Path.Combine(root, "second.zip");
            enqueue = Task.Run(() => automatic.TryEnqueue(new DroppedInstallBatchRequest([firstPath, secondPath])));
            await TestUiDispatcherHost.AwaitNotificationAsync(enqueueEntered.Task, enqueue, "pending-install.enqueue");

            PendingPackageMutationResult beforeWorker = await Install();
            Assert.IsFalse(beforeWorker.Succeeded);
            Assert.IsFalse(beforeWorker.ShouldApplyView);
            Assert.IsNull(dialogs.ConfirmationRequest);
            Assert.AreEqual(0, events.Count, "受付前に対象解決・確認・再生停止・store 変更を行わない。");
            Assert.IsFalse(automatic.TryEnqueue(new DroppedInstallBatchRequest([Path.Combine(root, "rejected.zip")])));
            releaseEnqueue.Set();
            Assert.IsTrue(await enqueue);
            await automatic.WaitForIdleAsync();

            CollectionAssert.AreEqual(new[] { firstPath, secondPath }, paths);
            Assert.AreEqual(1, rejectionsDuringBatch.Count);
            foreach (Task<PendingPackageMutationResult> attempt in rejectionsDuringBatch)
            {
                PendingPackageMutationResult rejection = await attempt;
                Assert.IsFalse(rejection.Succeeded, "各 batch の実行中は競合する保留導入を拒否する。");
                Assert.IsFalse(rejection.ShouldApplyView);
                Assert.IsNull(rejection.Failure, "Busy 拒否を実行失敗へ変換しない。");
            }
            CollectionAssert.AreEqual(new[] { (2, true) }, completionObservations,
                "一要求の複数入力の処理と受付解放を終えてから、一度だけ完了を通知します。");
            Assert.IsNull(dialogs.ConfirmationRequest);
            Assert.AreEqual(0, events.Count, "拒否した手動操作を後で実行しない。");
            Assert.AreEqual(2, dialogs.MessageRequests.Count);
            PendingPackageMutationResult fresh = await Install();
            Assert.IsTrue(fresh.Succeeded);
            Assert.AreEqual(1, events.Count(value => value == (manual ? "store-manual-install" : "store-force-install")));
        }
        finally
        {
            releaseEnqueue.Set();
            if (enqueue != null)
            {
                await enqueue;
            }

            if (automatic != null)
            {
                await automatic.WaitForIdleAsync();
            }

            Directory.Delete(root, recursive: true);
        }
    }

}
