using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryLr2SongDbSyncTestSupport;
using static BeMusicSeeker.TestSupport.Components.Lr2SongDbSyncTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryLr2SongDbSyncTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = TestSettingsFactory.CreateIsolatedSettings();

    /// <summary>
    /// LR2モード有効時だけテキスト列挙を必要とし、無効・設定不在時に不要なLR2入力取得を要求しない。
    /// </summary>
    [TestMethod]
    public void ShouldIncludeLr2TextSurface_OnlyWhenLr2ModeEnabled()
    {
        Assert.IsTrue(BMSLibrary.ShouldIncludeLr2TextSurface(new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = true,
        }));
        Assert.IsFalse(BMSLibrary.ShouldIncludeLr2TextSurface(new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = false,
        }));
        Assert.IsFalse(BMSLibrary.ShouldIncludeLr2TextSurface(null));
    }

    /// <summary>
    /// 導入後の通常フォルダ保存が失敗するとフォルダ反映全体を戻しても確定済みsongは保持し、不完全状態と後段失敗を報告する。
    /// </summary>
    /// <remarks>実SQLiteと管理主体を接続して保存結果または保存前の拒否を確認する。局所portだけではDB反映との順序を判定できない。</remarks>
    [TestMethod]
    public void InstallPendingPackages_RollsBackNormalFolderBatchAndKeepsCatalogCommit()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string sourceDirectory = Path.Combine(scope.DirectoryPath, "Pending");
            Directory.CreateDirectory(sourceDirectory);
            string sourceChartPath = Path.Combine(sourceDirectory, "chart.bms");
            File.WriteAllText(sourceChartPath, "#TITLE Added\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFile sourceFile = CreateSyncTestFile(sourceChartPath, ChartFileContentReader.ReadSnapshot(sourceChartPath));
            string destinationDirectory = Path.Combine(rootDirectory, "Pack", "Song");
            Directory.CreateDirectory(destinationDirectory);
            string destinationChartPath = Path.Combine(destinationDirectory, "chart.bms");
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.CreateTable<LR2SongDB.folder>();
                    setup.CreateTable<LR2SongDB.song>();
                    setup.Execute(
                        "CREATE TRIGGER fail_lr2_folder_insert BEFORE INSERT ON folder WHEN NEW.path LIKE '%Pack%' "
                        + "BEGIN SELECT RAISE(ABORT, 'forced normal-folder failure'); END;");
                });
            }

            ChartPackage package = ChartPackageTestExtensions.CreatePackage(
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                    sourceFile,
                    destinationDirectory));
            package.path = sourceDirectory;
            package.delete_parent = false;
            BmsLibraryOptionsSnapshot options = new()
            {
                OperationModeLR2DB = true,
                BMSInstallDir = rootDirectory,
                FolderNameFormat = "%TITLE%",
                DeletePendingPackageSourceAfterInstall = false,
                EnableSmartComponentOverwrite = false,
                KeepSmartOverwriteProtectedFilesByRenaming = false
            };
            var library = new TestBmsLibrary(scope.DatabaseScope,
                scope.SongDbPath,
                getLR2Config: null,
                _lr2ScoreDB: null,
                fileMutationService: new OwnedChartCollectionTestSupport.TestFileMutationService(),
                dialogService: new BmsLibraryInitializationTestSupport.RecordingDialogService(),
                uiScheduler: new TestUiScheduler(() => null!),
                optionsSnapshotProvider: () => options, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = [],
                BmsonCharts = [],
                ChartPackagesPending = new ObservableCollection<ChartPackage>([package]),
                ChartPackagesInstalled = new ObservableCollection<ChartPackage>()
            };

            try
            {
                PendingInstallBatchResult installResult = library.InstallPendingPackagesToEstimatedDestinationsWithReceipt(
                    [package]);
                LibraryMutationSessionReceipt sessionReceipt = installResult.SessionReceipt;
                Assert.IsTrue(installResult.HasDurableCommit);
                Assert.IsTrue(installResult.HasDurableFinalizationFailure);
                Assert.IsTrue(sessionReceipt.DurableCommit);
                Assert.IsNotNull(sessionReceipt.ApplyFailure);
                Assert.IsNull(sessionReceipt.FinalizationFailure);
                StringAssert.Contains(sessionReceipt.ApplyFailure.Message, "forced normal-folder failure");

                using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
                Assert.IsNotNull(verify.Find<LR2SongDB.song>(destinationChartPath));
                Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().Count());
                LR2SongDBExtended.lr2_song_db_sync_status status =
                    verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
                Assert.IsNotNull(status);
                Assert.AreEqual(Lr2SongDbSyncStatusKind.Incomplete.ToString(), status.status);
                Assert.AreEqual("lr2_normal_folder_mutation_sync_failed", status.stage);
            }
            finally
            {
                library.RequestShutdown("lr2-install-folder-sync-failure-test");
            }
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 進捗通知中に別進捗が入っても段階・件数・カーソルを同じ通知由来の組で公開し、混合スナップショットを防ぐ。
    /// </summary>
    [TestMethod]
    public void ProgressPublication_InterleavedOwnerUpdatesKeepStageAndCountsFromOneNotification()
    {
        using var scope = TestDatabaseScope.Create();
        var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, null, null, null, null, new InlineUiScheduler(), settings: testSettings);
        BMSLibrary.Lr2SynchronizationOwner owner = GetLr2SynchronizationOwner(library);
        var published = new List<Lr2SongDbSyncStatusSnapshot>();
        var saving = new Lr2SongDbSyncProgress
        {
            Stage = "song_rows_saving",
            StageProcessedCount = 0,
            StageTotalCount = 0,
            ProcessedCursor = 4,
            TotalCount = 20
        };
        var processing = new Lr2SongDbSyncProgress
        {
            Stage = "song_rows",
            StageProcessedCount = 7,
            StageTotalCount = 20,
            ProcessedCursor = 4,
            TotalCount = 20
        };
        bool interleaved = false;
        System.ComponentModel.PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(BMSLibrary.Lr2SongDbSyncStage) && !interleaved)
            {
                // 既存の観測プロパティ更新境界で別通知を完了させ、最初の通知の公開を再開する。
                interleaved = true;
                owner.UpdateProgress(processing);
                published.Add(library.GetLr2SongDbSyncStatusSnapshot());
            }
        };
        library.PropertyChanged += handler;
        try
        {
            owner.UpdateProgress(saving);
            published.Add(library.GetLr2SongDbSyncStatusSnapshot());

            Assert.IsTrue(interleaved);
            Assert.IsTrue(published.All(snapshot =>
                snapshot.ProcessedCursor == 4 && snapshot.TotalCount == 20
                && ((snapshot.Stage == saving.Stage && snapshot.StageProcessedCount == saving.StageProcessedCount
                        && snapshot.StageTotalCount == saving.StageTotalCount)
                    || (snapshot.Stage == processing.Stage && snapshot.StageProcessedCount == processing.StageProcessedCount
                        && snapshot.StageTotalCount == processing.StageTotalCount))));
        }
        finally
        {
            library.PropertyChanged -= handler;
        }
    }

    /// <summary>
    /// 実導入のカタログ保存失敗を購読経路からLR2不完全状態と元SQL原因へ反映し、失敗通知と保存状態の接続漏れを検出する。
    /// </summary>
    /// <remarks>実SQLiteと管理主体を接続して保存結果または保存前の拒否を確認する。局所portだけではDB反映との順序を判定できない。</remarks>
    [TestMethod]
    public void InstallPendingPackages_CatalogWriteFailurePublishesLr2IncompleteStatusThroughSubscription()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string sourceDirectory = Path.Combine(scope.DirectoryPath, "PendingCatalogFailure");
            Directory.CreateDirectory(sourceDirectory);
            string sourceChartPath = Path.Combine(sourceDirectory, "fixture.bms");
            File.WriteAllText(sourceChartPath, "#TITLE Catalog failure\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFile sourceFile = CreateSyncTestFile(sourceChartPath, ChartFileContentReader.ReadSnapshot(sourceChartPath));
            string destinationDirectory = Path.Combine(rootDirectory, "Pack", "Song");
            Directory.CreateDirectory(destinationDirectory);
            string destinationChartPath = Path.Combine(destinationDirectory, "fixture.bms");
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.CreateTable<LR2SongDB.song>();
                    setup.CreateTable<LR2SongDB.folder>();
                    string escapedPath = destinationChartPath.Replace("'", "''");
                    setup.Execute(
                        "CREATE TRIGGER fail_install_target_song_insert BEFORE INSERT ON song WHEN NEW.path = '"
                        + escapedPath
                        + "' BEGIN SELECT RAISE(ABORT, 'forced install-target song failure'); END;");
                });
            }
            BmsLibraryOptionsSnapshot options = new()
            {
                OperationModeLR2DB = true,
                ScanBmsFilesOnStartup = false,
                UpdateLr2IrRankingCacheOnStartup = false,
                EnableDownloadLr2IrScoreAndDetectUnsent = false,
                UseBeatorajaScoreDb = false,
                EnableReadOptimizedPragmas = false,
                PendingInstallEstimateMaxParallelPackages = 1
            };
            ChartPackage package = ChartPackageTestExtensions.CreatePackage(
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                    sourceFile,
                    destinationDirectory));
            package.path = sourceDirectory;
            package.delete_parent = false;
            var library = new TestBmsLibrary(scope.DatabaseScope,
                scope.SongDbPath,
                getLR2Config: null,
                _lr2ScoreDB: null,
                fileMutationService: new OwnedChartCollectionTestSupport.TestFileMutationService(),
                dialogService: new BmsLibraryInitializationTestSupport.RecordingDialogService(),
                uiScheduler: new TestUiScheduler(() => null!),
                optionsSnapshotProvider: () => options, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = [],
                BmsonCharts = [],
                ChartPackagesPending = new ObservableCollection<ChartPackage>([package]),
                ChartPackagesInstalled = new ObservableCollection<ChartPackage>()
            };
            int statusVersionBefore = library.Lr2SongDbSyncStatusVersion;

            try
            {
                PendingInstallBatchResult installResult = library.InstallPendingPackagesToEstimatedDestinationsWithReceipt(
                    [package]);
                Assert.IsFalse(installResult.HasDurableCommit);
                Assert.AreEqual(0, installResult.FailedPackages.Count);
                LibraryMutationSessionReceipt sessionReceipt = installResult.SessionReceipt;
                Assert.IsTrue(sessionReceipt.HasRequiredFailure);
                Assert.IsInstanceOfType(sessionReceipt.ApplyFailure, typeof(SQLite.SQLiteException));
                StringAssert.Contains(sessionReceipt.ApplyFailure.Message, "forced install-target song failure");

                using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
                LR2SongDBExtended.lr2_song_db_sync_status row =
                    verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
                Assert.IsNotNull(row);
                Assert.AreEqual(Lr2SongDbSyncStatusKind.Incomplete.ToString(), row.status);
                Assert.AreEqual("lr2_song_db_install_target_upsert_failed", row.stage);
                StringAssert.Contains(row.last_error, "forced install-target song failure");
                Assert.IsTrue(library.Lr2SongDbSyncStatusVersion > statusVersionBefore);
                Lr2SongDbSyncStatusSnapshot snapshot = library.GetLr2SongDbSyncStatusSnapshot();
                Assert.AreEqual(Lr2SongDbSyncStatusKind.Incomplete, snapshot.Status);
                Assert.AreEqual("lr2_song_db_install_target_upsert_failed", snapshot.Stage);
            }
            finally
            {
                library.RequestShutdown("lr2-file-diff-catalog-failure-test");
            }
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 準備開始前にRunningを公開し公開・準備中の再要求を同じ状態で返し、受理済み処理を二重に準備しない。
    /// </summary>
    [TestMethod]
    public async Task QueueLr2SongDbSync_PublishesRunningStatusBeforePreparingWithoutQueuingDuplicateWork()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            var library = new TestBmsLibrary(scope.DatabaseScope,
                scope.SongDbPath,
                getLR2Config: null,
                _lr2ScoreDB: null,
                fileMutationService: null,
                dialogService: new BmsLibraryInitializationTestSupport.RecordingDialogService(),
                uiScheduler: new InlineUiScheduler(), settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

            };
            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(library,
                BmsLibraryOptionsSnapshot.CreateCurrent(testSettings), [rootDirectory],
                CreateCompleteLr2ScanSurface([rootDirectory], [rootDirectory], [], [rootDirectory])));
            var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var releasePreparation = new ManualResetEventSlim();
            int prepareCallCount = 0;
            Lr2SongDbSyncStatusSnapshot? handoffCompetingStatus = null;
            library.PropertyChanged += (_, args) =>
            {
                if (string.Equals(
                        args.PropertyName,
                        nameof(BMSLibrary.Lr2SongDbSyncRequestedVersion),
                        StringComparison.Ordinal)
                    && library.Lr2SongDbSyncRequestedVersion > 0
                    && handoffCompetingStatus == null)
                {
                    handoffCompetingStatus = library.QueueLr2SongDbSyncAsync(
                        "test_prepare_handoff_competing_request",
                        force: false, initializationResult: initializationInput, preparedSurface: preparedInput).GetAwaiter().GetResult();
                }
            };

            Task<Lr2SongDbSyncStatusSnapshot> operation = library.QueueLr2SongDbSyncAsync(
                "test_prepare_order",
                force: false,
                (_, _) =>
                {
                    prepareCallCount++;
                    Assert.AreEqual(
                        Lr2SongDbSyncStatusKind.Running,
                        library.GetLr2SongDbSyncStatusSnapshot().Status);

                    Lr2SongDbSyncStatusSnapshot duplicate = library.QueueLr2SongDbSyncAsync(
                        "test_prepare_duplicate",
                        force: false,
                        (_, _) =>
                        {
                            prepareCallCount++;
                            return Lr2SongDbSyncPreparedDataSurface.Empty;
                        }, initializationResult: initializationInput, preparedSurface: preparedInput).GetAwaiter().GetResult();

                    Assert.AreEqual(Lr2SongDbSyncStatusKind.Running, duplicate.Status);
                    prepared.TrySetResult();
                    releasePreparation.Wait();
                    return Lr2SongDbSyncPreparedDataSurface.Empty;
                }, initializationResult: initializationInput, preparedSurface: preparedInput);

            try
            {
                await Task.WhenAny(prepared.Task, operation);
                if (!prepared.Task.IsCompleted) { await operation; Assert.Fail("実準備へ到達する前に終端しました。"); }
                Assert.IsFalse(operation.IsCompleted);
                Assert.IsTrue(library.OperationAdmission.IsActive);
                Assert.AreEqual(1, prepareCallCount);
                Assert.IsNotNull(handoffCompetingStatus);
                Lr2SongDbSyncStatusSnapshot completedHandoffStatus = handoffCompetingStatus!;
                Assert.AreEqual(Lr2SongDbSyncStatusKind.Running, completedHandoffStatus.Status);

                Assert.AreEqual(1, library.Lr2SongDbSyncRequestedVersion);
                Assert.IsTrue(library.Lr2SongDbSyncRunning);
            }
            finally
            {
                releasePreparation.Set();
                await operation;
            }
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 受理後の準備失敗は元例外・署名・原因を終端状態へ公開し、公開中の再要求を抑止して権を解放する。
    /// </summary>
    [TestMethod]
    public async Task QueueLr2SongDbSync_PreparationFailurePublishesRetryableTerminalStatus()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            var library = new TestBmsLibrary(scope.DatabaseScope,
                scope.SongDbPath,
                getLR2Config: null,
                _lr2ScoreDB: null,
                fileMutationService: null,
                dialogService: new BmsLibraryInitializationTestSupport.RecordingDialogService(),
                uiScheduler: new InlineUiScheduler(), settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

            };
            int scheduledWorkCount = 0;
            library.StartupBackgroundTaskScheduler = delegate
            {
                scheduledWorkCount++;
                return true;
            };
            Lr2SongDbSyncStatusSnapshot? competingStatus = null;
            library.PropertyChanged += (_, args) =>
            {
                if (string.Equals(
                        args.PropertyName,
                        nameof(BMSLibrary.Lr2SongDbSyncStatusVersion),
                        StringComparison.Ordinal)
                    && library.GetLr2SongDbSyncStatusSnapshot().Status == Lr2SongDbSyncStatusKind.Failed
                    && competingStatus == null)
                {
                    competingStatus = library.QueueLr2SongDbSyncAsync(
                        "test_prepare_failure_competing_request",
                        force: false).GetAwaiter().GetResult();
                }
            };
            var expectedFailure = new InvalidOperationException("expected preparation failure");

            InvalidOperationException failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => library.QueueLr2SongDbSyncAsync(
                "test_prepare_failure",
                force: false,
                (_, _) =>
                {
                    throw expectedFailure;
                }));

            Lr2SongDbSyncStatusSnapshot status = library.GetLr2SongDbSyncStatusSnapshot();
            Assert.AreSame(expectedFailure, failure);
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Failed, status.Status);
            Assert.AreEqual(expectedFailure.Message, status.LastError);
            Assert.IsNotNull(competingStatus);
            Lr2SongDbSyncStatusSnapshot completedCompetingStatus = competingStatus!;
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Failed, completedCompetingStatus.Status);
            Assert.AreEqual(0, scheduledWorkCount);
            Assert.AreEqual(1, library.Lr2SongDbSyncRequestedVersion, "受理した準備の失敗も同期要求として終端します。");
            Assert.IsFalse(library.OperationAdmission.IsActive);
            Assert.IsFalse(library.Lr2SongDbSyncRunning);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 三定義の実反映中は固定総数と単調進捗を保持し、購読先例外を隔離して保存完了後だけ差分完了を公開する。
    /// </summary>
    /// <remarks>実SQLiteと管理主体を接続して保存結果または保存前の拒否を確認する。局所portだけではDB反映との順序を判定できない。</remarks>
    [TestMethod]
    public void ReloadFileDiff_Lr2FolderFileApplyPreservesTotalAndIsolatesProgressSubscriber()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            string chartPath = Path.Combine(rootDirectory, "captured.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#TITLE Captured\r\n#ARTIST Artist\r\n#BPM 120\r\n#00111:01\r\n");
            string[] lr2FolderPaths = [.. Enumerable.Range(1, 3).Select(index =>
            {
                string path = Path.Combine(rootDirectory, "folder" + index + ".lr2folder");
                File.WriteAllText(path, "#TITLE Folder " + index + "\r\n");
                return path;
            })];
            BmsLibraryOptionsSnapshot options = new()
            {
                OperationModeLR2DB = true
            };
            IChartFileScanner chartFileScanner = CapturedChartFileScanner.FromFixture(
                [chartPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [rootDirectory] = []
                },
                [rootDirectory]);
            var library = new TestBmsLibrary(scope.DatabaseScope,
                scope.SongDbPath,
                getLR2Config: null,
                _lr2ScoreDB: null,
                startupRequiredFileScanReason: null,
                optionsSnapshotProvider: () => options,
                applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
                chartFileScanner: chartFileScanner,
                uiScheduler: new InlineUiScheduler(),
                rootFileEnumerator: new CapturedRootFileEnumerator(
                    lr2FolderPaths.ToDictionary(
                        path => path,
                        path => new RootFileEnumerationEntry(path, File.GetLastWriteTimeUtc(path)),
                        StringComparer.OrdinalIgnoreCase)), settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };
            var progressSnapshots = new List<BMSLibrary.LibraryInitializationProgressSnapshot>();
            int completionVersionAtFirstLr2Progress = -1;
            System.ComponentModel.PropertyChangedEventHandler progressObserver = (_, args) =>
            {
                if (string.Equals(
                        args.PropertyName,
                        nameof(BMSLibrary.LibraryInitializationProgressVersion),
                        StringComparison.Ordinal))
                {
                    BMSLibrary.LibraryInitializationProgressSnapshot snapshot =
                        library.GetLibraryInitializationProgressSnapshot();
                    if (snapshot.Stage == BMSLibrary.LibraryInitializationProgressStage.Lr2FolderFileCheck
                        && snapshot.TotalCount > 1)
                    {
                        progressSnapshots.Add(snapshot);
                        if (completionVersionAtFirstLr2Progress < 0)
                        {
                            completionVersionAtFirstLr2Progress = library.LibraryFileDiffCompletedVersion;
                        }
                    }
                }
            };
            int progressSubscriberFailureCount = 0;
            System.ComponentModel.PropertyChangedEventHandler throwingProgressSubscriber = (_, args) =>
            {
                if (string.Equals(
                        args.PropertyName,
                        nameof(BMSLibrary.LibraryInitializationProgressVersion),
                        StringComparison.Ordinal)
                    && Interlocked.CompareExchange(ref progressSubscriberFailureCount, 1, 0) == 0)
                {
                    throw new InvalidOperationException("forced LR2 file-diff progress subscriber failure");
                }
            };
            library.PropertyChanged += progressObserver;
            library.PropertyChanged += throwingProgressSubscriber;
            try
            {
                library.ReloadFileDiff();
            }
            finally
            {
                library.PropertyChanged -= throwingProgressSubscriber;
                library.PropertyChanged -= progressObserver;
            }

            Assert.AreEqual(1, progressSubscriberFailureCount);
            Assert.IsTrue(progressSnapshots.Count > 0);
            Assert.AreEqual(
                0,
                completionVersionAtFirstLr2Progress,
                "FileDiff completion must remain pending until the prepared LR2 folder-file apply succeeds.");
            Assert.IsTrue(progressSnapshots.Any(snapshot =>
                snapshot.TotalCount == lr2FolderPaths.Length
                && snapshot.ProcessedCount > 0
                && snapshot.ProcessedCount < snapshot.TotalCount));
            Assert.IsTrue(progressSnapshots.All(snapshot =>
                snapshot.TotalCount == lr2FolderPaths.Length
                && snapshot.ProcessedCount >= 0
                && snapshot.ProcessedCount <= snapshot.TotalCount));
            for (int index = 1; index < progressSnapshots.Count; index++)
            {
                Assert.IsTrue(
                    progressSnapshots[index].ProcessedCount >= progressSnapshots[index - 1].ProcessedCount,
                    "LR2 folder-file progress must be monotonic.");
            }
            Assert.IsTrue(progressSnapshots.Any(snapshot =>
                snapshot.TotalCount == lr2FolderPaths.Length
                && snapshot.ProcessedCount == snapshot.TotalCount));
            Assert.AreEqual(1, library.LibraryFileDiffCompletedVersion);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder[] folderRows = [.. verify.Table<LR2SongDB.folder>()];
            Assert.AreEqual(
                lr2FolderPaths.Length,
                folderRows.Count(row =>
                    string.Equals(
                        Path.GetExtension(row == null ? null : row.path),
                        ".lr2folder",
                        StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// UI通知の排出を止めても定義保存と差分完了を終え、排出後は同じ操作tokenの最終進捗を公開する。
    /// </summary>
    /// <remarks>実SQLiteと管理主体を接続して保存結果または保存前の拒否を確認する。局所portだけではDB反映との順序を判定できない。</remarks>
    [TestMethod]
    public void ReloadFileDiff_Lr2FolderCompletesDatabaseAndDiffBeforeDeferredPublication()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            string chartPath = Path.Combine(rootDirectory, "captured.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#TITLE Captured\r\n#ARTIST Artist\r\n#BPM 120\r\n#00111:01\r\n");
            string[] lr2FolderPaths = [.. Enumerable.Range(1, 3).Select(index =>
            {
                string path = Path.Combine(rootDirectory, "folder" + index + ".lr2folder");
                File.WriteAllText(path, "#TITLE Folder " + index + "\r\n");
                return path;
            })];
            BmsLibraryOptionsSnapshot options = new()
            {
                OperationModeLR2DB = true
            };
            IChartFileScanner chartFileScanner = CapturedChartFileScanner.FromFixture(
                [chartPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [rootDirectory] = []
                },
                [rootDirectory]);
            var scheduler = new QueuedUiScheduler();
            var library = new TestBmsLibrary(scope.DatabaseScope,
                scope.SongDbPath,
                getLR2Config: null,
                _lr2ScoreDB: null,
                startupRequiredFileScanReason: null,
                optionsSnapshotProvider: () => options,
                applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
                chartFileScanner: chartFileScanner,
                uiScheduler: scheduler,
                rootFileEnumerator: new CapturedRootFileEnumerator(
                    lr2FolderPaths.ToDictionary(
                        path => path,
                        path => new RootFileEnumerationEntry(path, File.GetLastWriteTimeUtc(path)),
                        StringComparer.OrdinalIgnoreCase)), settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };
            library.BeginLibraryInitializationProgressOperation(71L);
            var progressSnapshots = new List<BMSLibrary.LibraryInitializationProgressSnapshot>();
            System.ComponentModel.PropertyChangedEventHandler progressObserver = (_, args) =>
            {
                if (args.PropertyName == nameof(BMSLibrary.LibraryInitializationProgressVersion))
                {
                    progressSnapshots.AddRange(library.GetLibraryInitializationProgressSnapshots()
                        .Where(snapshot => snapshot.Stage == BMSLibrary.LibraryInitializationProgressStage.Lr2FolderFileCheck));
                }
            };
            library.PropertyChanged += progressObserver;
            try
            {
                library.ReloadFileDiff();
                Assert.IsTrue(scheduler.PendingCount > 0);
                Assert.AreEqual(0, progressSnapshots.Count);
                Assert.AreEqual(1, library.LibraryFileDiffCompletedVersion,
                    "表示の排出を止めても保存と差分完了は終結する。");
                using (LR2SongDBExtended beforePublication = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly())
                {
                    Assert.AreEqual(lr2FolderPaths.Length,
                        beforePublication.Table<LR2SongDB.folder>().Count(row => row.path.EndsWith(".lr2folder", StringComparison.OrdinalIgnoreCase)));
                }
                scheduler.Drain();
                BMSLibrary.LibraryInitializationProgressSnapshot latest = progressSnapshots.Last();
                Assert.AreEqual(71L, latest.OperationToken);
                Assert.AreEqual(lr2FolderPaths.Length, latest.TotalCount);
                Assert.AreEqual(latest.TotalCount, latest.ProcessedCount);
                Assert.AreEqual(1, library.LibraryFileDiffCompletedVersion);
            }
            finally
            {
                scheduler.Drain();
                library.PropertyChanged -= progressObserver;
            }

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder[] folderRows = [.. verify.Table<LR2SongDB.folder>()];
            Assert.AreEqual(
                lr2FolderPaths.Length,
                folderRows.Count(row =>
                    string.Equals(
                        Path.GetExtension(row == null ? null : row.path),
                        ".lr2folder",
                        StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 三定義を解析して固定総数・単調進捗を報告し、購読失敗を隔離して同時刻保持・欠落・空入力の各件数を区別する。
    /// </summary>
    [TestMethod]
    public void CreateLr2FolderFileSyncItems_ReportsFixedProgressAndIsolatesReporterFailure()
    {
        using var scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        Directory.CreateDirectory(rootDirectory);
        string[] filePaths = [.. Enumerable.Range(1, 3).Select(index =>
        {
            string path = Path.Combine(rootDirectory, "folder" + index + ".lr2folder");
            File.WriteAllText(path, "#TITLE Folder " + index + "\r\n");
            return path;
        })];
        Dictionary<string, RootFileEnumerationEntry> entriesByPath = filePaths.ToDictionary(
            path => path,
            path => new RootFileEnumerationEntry(path, File.GetLastWriteTimeUtc(path)));
        var request = new Lr2SongDbSyncRequest();
        var reports = new List<(int TotalCount, int ProcessedCount, string CurrentPath)>();

        Lr2SongDbSyncService.Lr2FolderFileSyncItemsResult result =
            Lr2SongDbSyncService.CreateLr2FolderFileSyncItems(
                filePaths,
                request,
                entriesByPath,
                progressReporter: (totalCount, processedCount, currentPath) =>
                {
                    reports.Add((totalCount, processedCount, currentPath));
                    throw new InvalidOperationException("observer failure");
                });

        Assert.IsFalse(result.HasReadFailures);
        Assert.AreEqual(filePaths.Length, result.Items.Count);
        Assert.AreEqual(filePaths.Length, result.ParseTargetCount);
        Assert.AreEqual(filePaths.Length, result.ParsedCount);
        Assert.AreEqual(0, result.UnchangedCount);
        Assert.IsTrue(reports.Count > 0);
        Assert.IsTrue(reports.All(report =>
            report.TotalCount == filePaths.Length
            && report.ProcessedCount >= 0
            && report.ProcessedCount <= report.TotalCount));
        Assert.IsTrue(reports.Any(report =>
            report.ProcessedCount > 0
            && report.ProcessedCount < report.TotalCount));
        Assert.IsTrue(reports.Any(report => report.ProcessedCount == report.TotalCount));
        for (int index = 1; index < reports.Count; index++)
        {
            Assert.IsTrue(reports[index].ProcessedCount >= reports[index - 1].ProcessedCount);
        }

        request.Lr2RootCustomFolderOutputBaseDir = rootDirectory;
        File.Delete(filePaths[2]);
        Lr2SongDbSyncService.Lr2FolderFileSyncItemsResult mixedResult =
            Lr2SongDbSyncService.CreateLr2FolderFileSyncItems(
                filePaths,
                request,
                entriesByPath,
                existingRowResolver: path => new LR2SongDB.folder
                {
                    path = path,
                    type = 2,
                    date = path == filePaths[0]
                        ? Lr2SongRowEnricher.ToLr2UnixSeconds(entriesByPath[path].LastWriteTimeUtc.GetValueOrDefault())
                        : -1,
                    parent = Lr2SongFolderParentNormalizer.RootParentHash
                });
        Assert.AreEqual(3, mixedResult.Items.Count);
        Assert.AreEqual(2, mixedResult.ParseTargetCount);
        Assert.AreEqual(1, mixedResult.ParsedCount);
        Assert.AreEqual(1, mixedResult.UnchangedCount);
        Assert.IsTrue(mixedResult.HasReadFailures);

        reports.Clear();
        Lr2SongDbSyncService.Lr2FolderFileSyncItemsResult emptyResult =
            Lr2SongDbSyncService.CreateLr2FolderFileSyncItems(
                [string.Empty, " ", "\t"],
                request,
                progressReporter: (totalCount, processedCount, currentPath) =>
                    reports.Add((totalCount, processedCount, currentPath)));

        Assert.AreEqual(0, emptyResult.Items.Count);
        Assert.AreEqual(0, emptyResult.ParseTargetCount);
        Assert.AreEqual(0, emptyResult.ParsedCount);
        Assert.AreEqual(0, emptyResult.UnchangedCount);
        Assert.AreEqual(0, reports.Count);
    }

    /// <summary>
    /// 定義保存の途中失敗は元SQL例外を返して全定義反映を戻し、最終進捗・差分完了を公開せず不完全状態を保持する。
    /// </summary>
    /// <remarks>実SQLiteと管理主体を接続して保存結果または保存前の拒否を確認する。局所portだけではDB反映との順序を判定できない。</remarks>
    [TestMethod]
    public void ReloadFileDiff_Lr2FolderApplyFailurePreservesExceptionAndDoesNotCompleteFileDiff()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            string chartPath = Path.Combine(rootDirectory, "captured.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#TITLE Captured\r\n#ARTIST Artist\r\n#BPM 120\r\n#00111:01\r\n");
            string[] lr2FolderPaths = [.. Enumerable.Range(1, 3).Select(index =>
            {
                string path = Path.Combine(rootDirectory, "folder" + index + ".lr2folder");
                File.WriteAllText(path, "#TITLE Folder " + index + "\r\n");
                return path;
            })];
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.CreateTable<LR2SongDB.folder>();
                    setup.Execute(
                        "CREATE TRIGGER fail_lr2_folder_file_diff BEFORE INSERT ON folder "
                        + "WHEN NEW.path LIKE '%folder2.lr2folder' "
                        + "BEGIN SELECT RAISE(ABORT, 'forced lr2 folder-file apply failure'); END;");
                });
            }
            BmsLibraryOptionsSnapshot options = new()
            {
                OperationModeLR2DB = true
            };
            IChartFileScanner chartFileScanner = CapturedChartFileScanner.FromFixture(
                [chartPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [rootDirectory] = []
                },
                [rootDirectory]);
            var library = new TestBmsLibrary(scope.DatabaseScope,
                scope.SongDbPath,
                getLR2Config: null,
                _lr2ScoreDB: null,
                startupRequiredFileScanReason: null,
                optionsSnapshotProvider: () => options,
                applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
                chartFileScanner: chartFileScanner,
                uiScheduler: new InlineUiScheduler(),
                rootFileEnumerator: new CapturedRootFileEnumerator(
                    lr2FolderPaths.ToDictionary(
                        path => path,
                        path => new RootFileEnumerationEntry(path, File.GetLastWriteTimeUtc(path)),
                        StringComparer.OrdinalIgnoreCase)), settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };
            var progressSnapshots = new List<BMSLibrary.LibraryInitializationProgressSnapshot>();
            int completionVersionAtFirstLr2Progress = -1;
            System.ComponentModel.PropertyChangedEventHandler progressObserver = (_, args) =>
            {
                if (!string.Equals(
                        args.PropertyName,
                        nameof(BMSLibrary.LibraryInitializationProgressVersion),
                        StringComparison.Ordinal))
                {
                    return;
                }

                BMSLibrary.LibraryInitializationProgressSnapshot snapshot =
                    library.GetLibraryInitializationProgressSnapshot();
                if (snapshot.Stage == BMSLibrary.LibraryInitializationProgressStage.Lr2FolderFileCheck
                    && snapshot.TotalCount > 1)
                {
                    progressSnapshots.Add(snapshot);
                    if (completionVersionAtFirstLr2Progress < 0)
                    {
                        completionVersionAtFirstLr2Progress = library.LibraryFileDiffCompletedVersion;
                    }
                }
            };
            library.PropertyChanged += progressObserver;
            try
            {
                SQLite.SQLiteException exception = Assert.ThrowsExactly<SQLite.SQLiteException>(
                    () => library.ReloadFileDiff());
                Assert.AreEqual("forced lr2 folder-file apply failure", exception.Message);
            }
            finally
            {
                library.PropertyChanged -= progressObserver;
            }

            Assert.IsTrue(progressSnapshots.Any(snapshot =>
                snapshot.TotalCount == lr2FolderPaths.Length
                && snapshot.ProcessedCount > 0
                && snapshot.ProcessedCount < snapshot.TotalCount));
            Assert.IsFalse(progressSnapshots.Any(snapshot =>
                snapshot.TotalCount == lr2FolderPaths.Length
                && snapshot.ProcessedCount == snapshot.TotalCount));
            Assert.AreEqual(0, completionVersionAtFirstLr2Progress);
            Assert.AreEqual(0, library.LibraryFileDiffCompletedVersion);

            Lr2SongDbSyncStatusSnapshot status = library.GetLr2SongDbSyncStatusSnapshot();
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Incomplete, status.Status);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder[] folderRows = [.. verify.Table<LR2SongDB.folder>()];
            Assert.IsFalse(folderRows.Any(row =>
                string.Equals(
                    Path.GetExtension(row?.path),
                    ".lr2folder",
                    StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 確定済み取込み入力を同期へ渡し、準備中に譜面と定義FSが変わっても旧タイトル・作者を保存して後発定義を混入させない。
    /// </summary>
    /// <remarks>実SQLiteと管理主体を接続して保存結果または保存前の拒否を確認する。局所portだけではDB反映との順序を判定できない。全件除外でも不完全folderと完了保存DB故障は非成功を保つ。</remarks>
    [TestMethod]
    [DataRow("completed")]
    [DataRow("incomplete_folder")]
    [DataRow("completed_status_failure")]
    public async Task ReloadFileDiff_PassesCommittedInputAfterCapturedSurfaceFilesystemChanges(string outcome)
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            string chartPath = Path.Combine(rootDirectory, "captured.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#TITLE Captured\r\n#ARTIST Artist\r\n#BPM 120\r\n#00111:01\r\n");

            BmsLibraryOptionsSnapshot options = new()
            {
                OperationModeLR2DB = true
            };
            IChartFileScanner chartFileScanner = CapturedChartFileScanner.FromFixture(
                [chartPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [rootDirectory] = []
                },
                [rootDirectory]);
            // Keep any accidental fresh LR2 folder enumeration fail-closed;
            // the explicit complete surface below is the captured production input.
            var library = new TestBmsLibrary(scope.DatabaseScope,
                scope.SongDbPath,
                getLR2Config: null,
                _lr2ScoreDB: null,
                startupRequiredFileScanReason: null,
                optionsSnapshotProvider: () => options,
                applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
                chartFileScanner: chartFileScanner, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings), uiScheduler: new TestUiScheduler(() => null))
            {

                BmsCharts = []
            };
            var synchronizationOwner =
                (BMSLibrary.Lr2SynchronizationOwner)library.Lr2Synchronization;
            int initialCatalogRevision = library.CatalogRevision;
            int observedNotificationVersion = 0;
            bool mutationLeaseWasAvailable = false;
            System.ComponentModel.PropertyChangedEventHandler notificationObserver = (_, args) =>
            {
                if (!string.Equals(
                        args.PropertyName,
                        nameof(BMSLibrary.CatalogRevision),
                        StringComparison.Ordinal))
                {
                    return;
                }

                observedNotificationVersion = library.CatalogRevision;
                using LibraryFileMutationLease reentryLease = synchronizationOwner.TryBeginMutation(
                    "test_reload_committed_receipt_post_lease_reentry",
                    showMessage: false);
                mutationLeaseWasAvailable |= reentryLease != null;
            };
            LibraryFileInitializationResult fileResult;
            library.PropertyChanged += notificationObserver;
            try
            {
                fileResult = library.ReloadFileDiff(null);
            }
            finally
            {
                library.PropertyChanged -= notificationObserver;
            }

            Assert.AreEqual(initialCatalogRevision + 1, library.CatalogRevision);
            Assert.AreEqual(library.CatalogRevision, observedNotificationVersion);
            Assert.IsTrue(mutationLeaseWasAvailable);

            // The native LR2 grouped scan is intentionally unavailable in this fixture.
            // Replace that incomplete physical result with the existing typed immutable
            // capture path so the queued coordinator must reuse this explicit surface.
            SongTableFileCheckResult scanSurface = CreateCompleteLr2ScanSurface(
                [rootDirectory], [rootDirectory], [], [rootDirectory]);
            scanSurface.Lr2ScanLr2FolderFileDiscoveryComplete = outcome != "incomplete_folder";
            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(
                library, options, [rootDirectory], scanSurface));
            fileResult = new(initializationInput.ScanSurface, fileResult.CommittedBmsPaths);
            Assert.IsTrue(fileResult.CommittedBmsPaths.Contains(chartPath));
            if (outcome == "completed_status_failure")
            {
                BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(library.DatabaseScope, scope.SongDbPath, setup =>
                {
                    BmsLibraryDbGateway.EnsureLr2SongDbSyncStatusSchema(setup);
                    setup.Execute("CREATE TRIGGER deny_completed_sync BEFORE INSERT ON lr2_song_db_sync_status"
                        + " WHEN NEW.status = 'Completed' BEGIN SELECT RAISE(ABORT, 'completed status failure'); END;");
                });
            }
            var statuses = new System.Collections.Concurrent.ConcurrentQueue<Lr2SongDbSyncStatusSnapshot>();
            library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(BMSLibrary.Lr2SongDbSyncStatusVersion))
                {
                    statuses.Enqueue(library.GetLr2SongDbSyncStatusSnapshot());
                }
            };
            Exception? terminalFailure = null;
            var preparationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var releasePreparation = new ManualResetEventSlim();
            Task<Lr2SongDbSyncStatusSnapshot> queuedOperation = library.QueueLr2SongDbSyncAsync(
                "test_reload_committed_receipt", force: false,
                prepareGeneratedData: (_, _) =>
                { preparationEntered.TrySetResult(); releasePreparation.Wait(); return Lr2SongDbSyncPreparedDataSurface.Empty; },
                initializationResult: fileResult, preparedSurface: preparedInput);
            string postCaptureFolderDirectory = Path.Combine(rootDirectory, "post-capture");
            string postCaptureFolderPath = Path.Combine(postCaptureFolderDirectory, "late.lr2folder");
            try
            {
                await Task.WhenAny(preparationEntered.Task, queuedOperation);
                if (!preparationEntered.Task.IsCompleted) { await queuedOperation; Assert.Fail("実準備へ到達しませんでした。"); }
                Assert.IsFalse(queuedOperation.IsCompleted);
                File.WriteAllText(chartPath,
                    "#PLAYER 1\r\n#TITLE Rewritten After Capture\r\n#ARTIST Rewritten Artist\r\n#BPM 120\r\n#00111:01\r\n");
                Directory.CreateDirectory(postCaptureFolderDirectory);
                File.WriteAllText(postCaptureFolderPath, "#TITLE Late Candidate");
            }
            finally
            {
                releasePreparation.Set();
                try { await queuedOperation; }
                catch (Exception ex) { terminalFailure = ex; }
            }

            Assert.IsFalse(library.OperationAdmission.IsActive);
            Assert.IsFalse(synchronizationOwner.PlaylistOperationAdmission.IsActive);
            Assert.IsFalse(synchronizationOwner.Running);
            Assert.IsFalse(statuses.IsEmpty);
            Assert.IsFalse(statuses.Any(snapshot => snapshot.Stage?.StartsWith("song_rows", StringComparison.Ordinal) == true));
            if (outcome == "completed") { Assert.IsNull(terminalFailure); }
            else { Assert.IsNotNull(terminalFailure); }
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            LR2SongDBExtended.lr2_song_db_sync_status status =
                verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(status);
            Assert.AreEqual(outcome == "completed" ? "Completed" : "Failed", status.status);
            Assert.AreEqual(1, status.total_count, "全体件数はフォルダ分だけです。");
            if (outcome == "completed")
            {
                Assert.AreEqual(1, status.processed_cursor);
                Assert.IsNotNull(status.completed_at);
                Assert.IsTrue(statuses.Any(snapshot => snapshot.Stage == "final_validation"));
                Assert.IsTrue(statuses.Any(snapshot => snapshot.Stage == "sync_state_saving"));
                Assert.IsNotNull(verify.Find<LR2SongDB.folder>(ToFolderPath(rootDirectory)));
            }
            else { Assert.IsNull(status.completed_at); }
            if (outcome == "completed_status_failure")
            {
                Assert.IsNotNull(terminalFailure);
                StringAssert.Contains(terminalFailure.Message, "completed status failure");
                Assert.AreEqual(1, status.processed_cursor);
                Assert.IsTrue(statuses.Any(snapshot => snapshot.Stage == "sync_state_saving"));
                Assert.IsNotNull(verify.Find<LR2SongDB.folder>(ToFolderPath(rootDirectory)));
            }
            LR2SongDB.song persistedSong = verify.Table<LR2SongDB.song>().Single(row =>
                string.Equals(row?.path, chartPath, StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual("Captured", persistedSong.title);
            Assert.AreEqual("Artist", persistedSong.artist);
            Assert.IsFalse(verify.Table<LR2SongDB.folder>().Any(row =>
                string.Equals(row?.path, postCaptureFolderPath, StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 二つの準備面を合流し、定義・folderinfo・ディレクトリ・テキスト対象の双方を残す。片側だけを採用する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void PreparedDataSurfaceMerge_KeepsMetadataSurfaces()
    {
        using var scope = TestDatabaseScope.Create();
        string playlistDirectory = Path.Combine(scope.DirectoryPath, "Playlist");
        string builtinDirectory = Path.Combine(scope.DirectoryPath, "LR2files", "CustomFolder", "RANDOM");
        Directory.CreateDirectory(playlistDirectory);
        Directory.CreateDirectory(builtinDirectory);
        string playlistLr2FolderPath = Path.Combine(playlistDirectory, "0000.lr2folder");
        string builtinFolderInfoPath = Path.Combine(builtinDirectory, "folderinfo.txt");
        File.WriteAllText(playlistLr2FolderPath, "#TITLE Playlist", Encoding.GetEncoding("shift_jis"));
        File.WriteAllText(builtinFolderInfoPath, "#TITLE Random", Encoding.GetEncoding("shift_jis"));
        DateTime playlistTimestamp = new(2026, 6, 8, 1, 0, 0, DateTimeKind.Utc);
        DateTime builtinTimestamp = new(2026, 6, 8, 2, 0, 0, DateTimeKind.Utc);

        var merged = Lr2SongDbSyncPreparedDataSurface.Merge(
            CreatePreparedLr2FolderSurface(
                playlistDirectory,
                playlistLr2FolderPath,
                new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [playlistDirectory] = new RootFileEnumerationEntry(playlistDirectory, playlistTimestamp)
                }),
            new Lr2SongDbSyncPreparedDataSurface(
                [builtinDirectory],
                [],
                new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [builtinDirectory] = new RootFileEnumerationEntry(builtinDirectory, builtinTimestamp)
                },
                [builtinFolderInfoPath],
                new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [builtinFolderInfoPath] = new RootFileEnumerationEntry(builtinFolderInfoPath, builtinTimestamp)
                },
                [builtinDirectory],
                discoveryComplete: true));

        CollectionAssert.Contains(merged.Lr2FolderFilePaths.ToList(), playlistLr2FolderPath);
        Assert.IsTrue(merged.DirectoryEntries.ContainsKey(Lr2FolderPath.NormalizeDirectoryPath(playlistDirectory)));
        Assert.IsTrue(merged.DirectoryEntries.ContainsKey(Lr2FolderPath.NormalizeDirectoryPath(builtinDirectory)));
        CollectionAssert.Contains(merged.FolderInfoFilePaths.ToList(), builtinFolderInfoPath);
        CollectionAssert.Contains(merged.TextFileDirectories.ToList(), Lr2FolderPath.NormalizeDirectoryPath(builtinDirectory));
    }

    /// <summary>
    /// 検索rootと親子に重なる探索先を除き、root外の独立したテキスト入力先だけを返す。
    /// </summary>
    [TestMethod]
    public void CreateLr2TextMetadataSourceDirectoriesOutsideRoots_ExcludesOverlappingDiscoveryRoots()
    {
        using var scope = TestDatabaseScope.Create();
        string parentDirectory = Path.Combine(scope.DirectoryPath, "Library");
        string rootDirectory = Path.Combine(parentDirectory, "BMS");
        string childDirectory = Path.Combine(rootDirectory, "Nested");
        string outsideDirectory = Path.Combine(scope.DirectoryPath, "LR2files", "CustomFolder");
        IReadOnlyList<string> result = Lr2SongDbSyncInputSurfaceHelper.CreateLr2TextMetadataSourceDirectoriesOutsideRoots(
            new[] { parentDirectory, rootDirectory, childDirectory, outsideDirectory },
            new[] { rootDirectory });

        CollectionAssert.AreEqual(
            new[] { Lr2FolderPath.NormalizeDirectoryPath(outsideDirectory) },
            result.ToArray());
    }

    /// <summary>
    /// UI投稿を保留してもDBの定義反映を先に完了し、解放後は最新の完了状態を表示する。購読例外でも保存成功を失わない。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public async Task QueueLr2SongDbSync_CompletesFolderReconciliationBeforeLatestStatusPublication()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "FolderProgressRoot");
            string firstDirectory = Path.Combine(rootDirectory, "First");
            string secondDirectory = Path.Combine(rootDirectory, "Second");
            Directory.CreateDirectory(firstDirectory);
            Directory.CreateDirectory(secondDirectory);
            var scheduler = new QueuedUiScheduler();
            var library = new TestBmsLibrary(scope.DatabaseScope,
                scope.SongDbPath,
                null,
                null,
                null,
                null,
                scheduler, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };
            BmsLibraryOptionsSnapshot options = new()
            {
                OperationModeLR2DB = true
            };
            Dictionary<string, RootFileEnumerationEntry> directoryEntries =
                CreateDirectoryEntryMap(rootDirectory, firstDirectory, secondDirectory);
            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(
                library,
                options,
                [rootDirectory],
                new SongTableFileCheckResult
                {
                    Lr2ScanSurfaceAvailable = true,
                    Lr2ScanNormalFolderDirectoryPaths = [rootDirectory, firstDirectory, secondDirectory],
                    Lr2ScanNormalFolderDirectoryEntries = directoryEntries,
                    Lr2ScanDirectoryEntries = directoryEntries,
                    Lr2ScanFolderInfoFilePaths = [],
                    Lr2ScanFolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                    Lr2ScanTextFileDirectories = [],
                    Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory],
                    Lr2ScanLr2FolderFilePaths = [],
                    Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                    Lr2ScanLr2FolderFileDiscoveryComplete = true
                }));

            var timeline = new List<Lr2SongDbSyncStatusSnapshot>();
            System.ComponentModel.PropertyChangedEventHandler timelineSubscriber = (_, args) =>
            {
                if (string.Equals(
                    args.PropertyName,
                    nameof(BMSLibrary.Lr2SongDbSyncStatusVersion),
                    StringComparison.Ordinal))
                {
                    Lr2SongDbSyncStatusSnapshot snapshot = library.GetLr2SongDbSyncStatusSnapshot();
                    timeline.Add(snapshot);
                }
            };

            library.PropertyChanged += timelineSubscriber; int subscriberFailureCount = 0;
            System.ComponentModel.PropertyChangedEventHandler throwingSubscriber = (_, args) =>
            {
                if (string.Equals(
                        args.PropertyName,
                        nameof(BMSLibrary.Lr2SongDbSyncStatusVersion),
                        StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref subscriberFailureCount);
                    throw new InvalidOperationException("forced LR2 status observer failure");
                }
            };
            library.PropertyChanged += throwingSubscriber;

            try
            {
                await library.QueueLr2SongDbSyncAsync("folder_reconciliation_progress", initializationResult: initializationInput, preparedSurface: preparedInput);

                Assert.IsTrue(scheduler.PendingCount > 0);
                Assert.AreEqual(0, timeline.Count);
                Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed, library.GetLr2SongDbSyncStatusSnapshot().Status);
                Assert.AreEqual(1, library.Lr2SongDbSyncCompletedVersion);
                string[] expectedFolderPaths = [.. new[] { rootDirectory, firstDirectory, secondDirectory }.Select(ToFolderPath)];
                using (LR2SongDBExtended beforePublication = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly())
                {
                    CollectionAssert.AreEquivalent(expectedFolderPaths,
                        beforePublication.Table<LR2SongDB.folder>().Select(row => row.path).ToArray());
                }

                scheduler.Drain();

                Assert.IsTrue(timeline.Count > 0);
                Assert.IsTrue(timeline.All(snapshot => snapshot.Status == Lr2SongDbSyncStatusKind.Completed));
                Assert.IsTrue(subscriberFailureCount > 0);
                Lr2SongDbSyncStatusSnapshot completed = library.GetLr2SongDbSyncStatusSnapshot();
                Assert.AreEqual(completed.TotalCount, completed.ProcessedCursor);
                Assert.AreEqual(0, scheduler.PendingCount);
                using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
                CollectionAssert.AreEquivalent(expectedFolderPaths,
                    verify.Table<LR2SongDB.folder>().Select(row => row.path).ToArray());
            }
            finally
            {
                try { scheduler.Drain(); }
                finally
                {
                    library.PropertyChanged -= timelineSubscriber;
                    library.PropertyChanged -= throwingSubscriber;
                }
            }
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 定義反映が失敗するとDBを巻き戻し、UI解放後に元失敗を持つ最新状態を公開する。成功の誤表示を検出する。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public async Task QueueLr2SongDbSync_PublishesLatestFailureAndPreservesDatabaseAfterApplyFailure()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "FolderProgressFailureRoot");
            string childDirectory = Path.Combine(rootDirectory, "Child");
            Directory.CreateDirectory(childDirectory);
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.CreateTable<LR2SongDB.folder>();
                    setup.Execute(
                        "CREATE TRIGGER fail_folder_reconciliation BEFORE INSERT ON folder "
                        + "BEGIN SELECT RAISE(ABORT, 'forced folder table apply failure'); END;");
                });
            }

            var scheduler = new QueuedUiScheduler();
            var library = new TestBmsLibrary(scope.DatabaseScope,
                scope.SongDbPath,
                null,
                null,
                null,
                null,
                scheduler, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };
            BmsLibraryOptionsSnapshot options = new()
            {
                OperationModeLR2DB = true
            };
            Dictionary<string, RootFileEnumerationEntry> directoryEntries =
                CreateDirectoryEntryMap(rootDirectory, childDirectory);
            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(
                library,
                options,
                [rootDirectory],
                new SongTableFileCheckResult
                {
                    Lr2ScanSurfaceAvailable = true,
                    Lr2ScanNormalFolderDirectoryPaths = [rootDirectory, childDirectory],
                    Lr2ScanNormalFolderDirectoryEntries = directoryEntries,
                    Lr2ScanDirectoryEntries = directoryEntries,
                    Lr2ScanFolderInfoFilePaths = [],
                    Lr2ScanFolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                    Lr2ScanTextFileDirectories = [],
                    Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory],
                    Lr2ScanLr2FolderFilePaths = [],
                    Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                    Lr2ScanLr2FolderFileDiscoveryComplete = true
                }));

            var timeline = new List<Lr2SongDbSyncStatusSnapshot>();
            System.ComponentModel.PropertyChangedEventHandler timelineSubscriber = (_, args) =>
            {
                if (string.Equals(
                    args.PropertyName,
                    nameof(BMSLibrary.Lr2SongDbSyncStatusVersion),
                    StringComparison.Ordinal))
                {
                    Lr2SongDbSyncStatusSnapshot snapshot = library.GetLr2SongDbSyncStatusSnapshot();
                    timeline.Add(snapshot);
                }
            };

            library.PropertyChanged += timelineSubscriber;
            try
            {
                await Assert.ThrowsExactlyAsync<SQLite.SQLiteException>(() => library.QueueLr2SongDbSyncAsync("folder_reconciliation_failure", initializationResult: initializationInput, preparedSurface: preparedInput));

                Lr2SongDbSyncStatusSnapshot latest = library.GetLr2SongDbSyncStatusSnapshot();
                Assert.AreEqual(Lr2SongDbSyncStatusKind.Failed, latest.Status);
                StringAssert.Contains(latest.LastError, "forced folder table apply failure");
                Assert.AreEqual(0, library.Lr2SongDbSyncCompletedVersion);
                Assert.IsTrue(scheduler.PendingCount > 0);
                using (LR2SongDBExtended beforePublication = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly())
                {
                    Assert.AreEqual(0, beforePublication.Table<LR2SongDB.folder>().Count());
                }

                Assert.AreEqual(0, timeline.Count);
                scheduler.Drain();

                Assert.IsTrue(timeline.Count > 0);
                Assert.IsTrue(timeline.All(snapshot => snapshot.Status == Lr2SongDbSyncStatusKind.Failed));
                Assert.IsTrue(timeline.All(snapshot => snapshot.LastError == latest.LastError));
                Assert.AreEqual(0, scheduler.PendingCount);
                Assert.AreEqual(0, library.Lr2SongDbSyncCompletedVersion);

                using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
                Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().Count());
            }
            finally
            {
                try { scheduler.Drain(); }
                finally
                {
                    library.PropertyChanged -= timelineSubscriber;
                }
            }
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 連続20回の状態更新を一つのUI投稿へ合流し、通知時には最後の段階・件数・実行状態を組として公開する。
    /// </summary>
    [TestMethod]
    public void Lr2PropertyPublication_CoalescesRepeatedChangesIntoOneUiDrain()
    {
        using var scope = TestDatabaseScope.Create();
        var scheduler = new QueuedUiScheduler();
        var library = new TestBmsLibrary(scope.DatabaseScope,
            scope.SongDbPath,
            null,
            null,
            null,
            null,
            scheduler, settings: testSettings);
        BMSLibrary.Lr2SynchronizationOwner owner = GetLr2SynchronizationOwner(library);
        var publishedPropertyNames = new List<string>();
        System.ComponentModel.PropertyChangedEventHandler subscriber = (_, args) => publishedPropertyNames.Add(args.PropertyName!);
        library.PropertyChanged += subscriber;
        try
        {
            string[] expectedPropertyNames =
            [
                nameof(BMSLibrary.Lr2SongDbSyncStage),
                nameof(BMSLibrary.Lr2SongDbSyncProcessedCount),
                nameof(BMSLibrary.Lr2SongDbSyncTotalCount),
                nameof(BMSLibrary.Lr2SongDbSyncStageProcessedCount),
                nameof(BMSLibrary.Lr2SongDbSyncStageTotalCount),
                nameof(BMSLibrary.Lr2SongDbSyncStatusVersion)
            ];

            for (int iteration = 0; iteration < 20; iteration++)
            {
                owner.UpdateLr2SongDbSyncProgress(new Lr2SongDbSyncProgress
                {
                    TotalCount = iteration + 1,
                    ProcessedCursor = iteration,
                    Stage = "stage_" + iteration,
                    StageProcessedCount = iteration,
                    StageTotalCount = iteration + 1
                });
            }

            Assert.AreEqual(1, scheduler.ScheduleCount);
            Assert.AreEqual(1, scheduler.PendingCount);

            scheduler.Drain();

            Assert.AreEqual(0, scheduler.PendingCount);
            CollectionAssert.AreEquivalent(expectedPropertyNames, publishedPropertyNames);
            Lr2SongDbSyncStatusSnapshot latest = library.GetLr2SongDbSyncStatusSnapshot();
            Assert.AreEqual("stage_19", latest.Stage);
            Assert.AreEqual(19, latest.ProcessedCursor);
            Assert.AreEqual(20, latest.TotalCount);
            Assert.AreEqual(19, latest.StageProcessedCount);
            Assert.AreEqual(20, latest.StageTotalCount);
        }
        finally
        {
            try { scheduler.Drain(); }
            finally { library.PropertyChanged -= subscriber; }
        }
    }

    /// <summary>
    /// 通知中の再更新は次のUIターンへ送り、現在の通知組を途中で混ぜず追加投稿で公開する。
    /// </summary>
    [TestMethod]
    public void Lr2PropertyPublication_RefillDuringDrainUsesNextUiTurn()
    {
        using var scope = TestDatabaseScope.Create();
        var scheduler = new QueuedUiScheduler();
        var library = new TestBmsLibrary(scope.DatabaseScope,
            scope.SongDbPath,
            null,
            null,
            null,
            null,
            scheduler, settings: testSettings);
        BMSLibrary.Lr2SynchronizationOwner owner = GetLr2SynchronizationOwner(library);
        string firstPropertyName = nameof(BMSLibrary.Lr2SongDbSyncStage);
        string refilledPropertyName = nameof(BMSLibrary.Lr2SongDbSyncProcessedCount);
        string statusPropertyName = nameof(BMSLibrary.Lr2SongDbSyncStatusVersion);
        var publishedPropertyNames = new List<string>();
        bool refilled = false;
        System.ComponentModel.PropertyChangedEventHandler subscriber = (_, args) =>
        {
            publishedPropertyNames.Add(args.PropertyName!);
            if (!refilled && args.PropertyName == firstPropertyName)
            {
                refilled = true;
                owner.UpdateLr2SongDbSyncProgress(new Lr2SongDbSyncProgress
                {
                    Stage = "first",
                    ProcessedCursor = 1
                });
            }
        };
        library.PropertyChanged += subscriber;
        try
        {
            owner.UpdateLr2SongDbSyncProgress(new Lr2SongDbSyncProgress
            {
                Stage = "first"
            });

            scheduler.ExecuteNext();

            CollectionAssert.AreEqual(new[] { firstPropertyName, statusPropertyName }, publishedPropertyNames);
            Assert.AreEqual(2, scheduler.ScheduleCount);
            Assert.AreEqual(1, scheduler.PendingCount);

            scheduler.ExecuteNext();

            CollectionAssert.AreEqual(
                new[] { firstPropertyName, statusPropertyName, refilledPropertyName, statusPropertyName },
                publishedPropertyNames);
            Assert.AreEqual(0, scheduler.PendingCount);
            Assert.AreEqual(1, library.GetLr2SongDbSyncStatusSnapshot().ProcessedCursor);
        }
        finally
        {
            try { scheduler.Drain(); }
            finally { library.PropertyChanged -= subscriber; }
        }
    }

    /// <summary>
    /// 受理後の終了要求は実Taskを取消終端させ、再開可能なIncompleteと確定済み段階・件数をDBへ保存する。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public async Task QueueLr2SongDbSync_ShutdownMarksDurableIncompleteStatus()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        using var inputSurfaceGate = new ManualResetEventSlim();
        Task<Lr2SongDbSyncStatusSnapshot>? queuedOperation = null;
        BMSLibrary? library = null;
        var scheduler = new QueuedUiScheduler();
        Exception? testFailure = null;
        var inputSurfaceEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            string durableFolderPath = ToFolderPath(rootDirectory);
            string durableSongPath = Path.Combine(rootDirectory, "durable.bms");
            using (LR2SongDBExtended seed = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                seed.RunInTransaction(() =>
                {
                    seed.CreateTable<LR2SongDB.song>();
                    seed.CreateTable<LR2SongDB.folder>();
                    seed.InsertOrReplace(new LR2SongDB.folder
                    {
                        path = durableFolderPath,
                        type = 1,
                        title = "Durable folder before shutdown",
                        date = 654320,
                        adddate = 654321
                    }, typeof(LR2SongDB.folder));
                    seed.InsertOrReplace(new LR2SongDB.song
                    {
                        path = durableSongPath,
                        title = "Durable song before shutdown",
                        artist = "Durable artist before shutdown",
                        favorite = 6,
                        adddate = 654321,
                        tag = "durable-user-tag"
                    }, typeof(LR2SongDB.song));
                });
            }
            var options = BmsLibraryOptionsSnapshot.CreateCurrent(testSettings);
            // 実同期workerの開始通知で待ち、設定の再取得や表示の排出を終結条件にしません。
            library = new TestBmsLibrary(scope.DatabaseScope,
                scope.SongDbPath,
                getLR2Config: null,
                _lr2ScoreDB: null,
                fileMutationService: null,
                dialogService: null,
                uiScheduler: scheduler,
                optionsSnapshotProvider: () => options, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };
            library.StartupBackgroundTaskReporter = (name, status, _, _, _) =>
            {
                if (name != "lr2_song_db_sync" || status != "start") { return; }
                inputSurfaceEntered.TrySetResult(true);
                inputSurfaceGate.Wait();
            };
            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(
                library,
                options,
                [rootDirectory],
                CreateCompleteLr2ScanSurface(
                    [rootDirectory],
                    [rootDirectory],
                    [],
                    [rootDirectory])));
            queuedOperation = library.QueueLr2SongDbSyncAsync("test_preflight_shutdown", force: true,
                prepareGeneratedData: (_, _) =>
                {
                    return Lr2SongDbSyncPreparedDataSurface.Empty;
                }, initializationResult: initializationInput, preparedSurface: preparedInput);
            await TestUiDispatcherHost.AwaitNotificationAsync(inputSurfaceEntered.Task, queuedOperation, "lr2.shutdown.worker-entered");
            library.RequestShutdown("test_preflight_shutdown");
            inputSurfaceGate.Set();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => queuedOperation);

            Assert.IsFalse(library.Lr2SongDbSyncRunning);
            Lr2SongDbSyncStatusSnapshot runtimeStatus = library.GetLr2SongDbSyncStatusSnapshot();
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            LR2SongDBExtended.lr2_song_db_sync_status row = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual("Incomplete", row.status);
            Assert.IsFalse(string.IsNullOrWhiteSpace(runtimeStatus.Stage));
            Assert.AreEqual(runtimeStatus.Stage, row.stage);
            Assert.AreEqual(0, row.processed_cursor);
            Assert.IsTrue(runtimeStatus.ProcessedCursor.HasValue);
            Assert.IsTrue(runtimeStatus.TotalCount.HasValue);
            Assert.AreEqual(runtimeStatus.ProcessedCursor, row.processed_cursor);
            Assert.AreEqual(runtimeStatus.TotalCount, row.total_count);
            Assert.AreEqual("shutdown_interrupted", row.last_error);
            LR2SongDB.folder durableFolder = verify.Find<LR2SongDB.folder>(durableFolderPath);
            Assert.IsNotNull(durableFolder);
            Assert.AreEqual("Durable folder before shutdown", durableFolder.title);
            Assert.AreEqual(654320, durableFolder.date);
            Assert.AreEqual(654321, durableFolder.adddate);
            LR2SongDB.song durableSong = verify.Find<LR2SongDB.song>(durableSongPath);
            Assert.IsNotNull(durableSong);
            Assert.AreEqual("Durable song before shutdown", durableSong.title);
            Assert.AreEqual("Durable artist before shutdown", durableSong.artist);
            Assert.AreEqual(6, durableSong.favorite);
            Assert.AreEqual(654321, durableSong.adddate);
            Assert.AreEqual("durable-user-tag", durableSong.tag);
        }
        catch (Exception ex)
        {
            testFailure = ex;
            throw;
        }
        finally
        {
            inputSurfaceGate.Set();
            library?.RequestShutdown("shutdown-fixture-cleanup");
            try
            {
                if (queuedOperation != null)
                {
                    try { await queuedOperation; }
                    catch (OperationCanceledException) { }
                }
            }
            catch (Exception cleanupFailure)
            {
                if (testFailure is null) { throw; }
                System.Diagnostics.Trace.WriteLine("後片付けの二次失敗: " + cleanupFailure);
            }
            finally
            {
                try { scheduler.Drain(); }
                finally { ResetTouchedSettings(testSettings); }
            }
        }
    }

    /// <summary>
    /// 文字コード判定がUnknownでも既定の解析でASCIIタイトルを読めることを確認し、Unknownを一律解析不能にする誤りを検出する。
    /// </summary>
    [TestMethod]
    public void SnapshotSongRowParser_AllowsUnknownEncodingToUseParserDefault()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("#TITLE unknown fallback\r\n");
        var snapshot = new ChartFileSnapshot(
            @"D:\BMS\Unknown\chart.bms",
            bytes,
            new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc),
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            new string('b', 64));
        var detectionResult = new BmsEncodingDetectionResult(
            "unknown",
            BmsEncodingDetectionOutcome.Unknown,
            fastAscii: false,
            decodedText: null);

        ChartFile song = BmsChartFileParser.ParseSnapshot(snapshot, detectionResult);

        Assert.AreEqual("unknown fallback", song.RawTitle);
    }

    /// <summary>
    /// 捕捉した複数folderinfoから対象ディレクトリだけを選び、時刻と探索完全性を保持する。
    /// </summary>
    [TestMethod]
    public void FolderInfoCandidateSnapshotFromEntries_ReturnsTargetFolderInfoMetadata()
    {
        using var scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        string chartDirectory = Path.Combine(rootDirectory, "Pack");
        string unrelatedDirectory = Path.Combine(rootDirectory, "Other");
        string folderInfoPath = Path.Combine(chartDirectory, "folderinfo.txt");
        string unrelatedFolderInfoPath = Path.Combine(unrelatedDirectory, "folderinfo.txt");
        DateTime folderInfoTimestamp = new(2026, 6, 5, 6, 0, 0, DateTimeKind.Utc);

        Lr2FolderInfoCandidateSnapshot snapshot = Lr2FolderInfoCandidateEnumerationService.CreateSnapshotFromEntries(
            [
                new RootFileEnumerationEntry(folderInfoPath, folderInfoTimestamp),
                new RootFileEnumerationEntry(unrelatedFolderInfoPath, folderInfoTimestamp)
            ],
            [chartDirectory]);

        CollectionAssert.AreEqual(new[] { folderInfoPath }, snapshot.Paths.ToArray());
        Assert.IsTrue(snapshot.DiscoveryComplete);
        Assert.IsTrue(snapshot.EntriesByPath.TryGetValue(folderInfoPath, out RootFileEnumerationEntry? entry));
        Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(folderInfoTimestamp), entry!.LastWriteTimeUnixSeconds);
    }

    /// <summary>
    /// folderinfoと一般txtが混在しても対象folderinfoと対象テキストディレクトリを分け、別ディレクトリを混入させない。
    /// </summary>
    [TestMethod]
    public void TextMetadataCandidateSnapshotFromEntries_ReturnsTargetFolderInfoAndTextDirectories()
    {
        using var scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        string chartDirectory = Path.Combine(rootDirectory, "Pack");
        string unrelatedDirectory = Path.Combine(rootDirectory, "Other");
        string folderInfoPath = Path.Combine(chartDirectory, "folderinfo.txt");
        string readmePath = Path.Combine(chartDirectory, "readme.txt");
        string unrelatedTextPath = Path.Combine(unrelatedDirectory, "readme.txt");
        DateTime folderInfoTimestamp = new(2026, 6, 5, 6, 0, 0, DateTimeKind.Utc);
        Lr2FolderInfoCandidateSnapshot folderInfoSnapshot = Lr2FolderInfoCandidateEnumerationService.CreateSnapshotFromEntries(
            [
                new RootFileEnumerationEntry(folderInfoPath, folderInfoTimestamp),
                new RootFileEnumerationEntry(readmePath, folderInfoTimestamp),
                new RootFileEnumerationEntry(unrelatedTextPath, folderInfoTimestamp)
            ],
            [chartDirectory]);
        Lr2TextMetadataCandidateSnapshot snapshot = new(folderInfoSnapshot, [chartDirectory]);

        CollectionAssert.AreEqual(new[] { folderInfoPath }, snapshot.FolderInfoCandidates.Paths.ToArray());
        CollectionAssert.AreEqual(new[] { chartDirectory }, snapshot.TextFileDirectories.ToArray());
        CollectionAssert.DoesNotContain(snapshot.TextFileDirectories.ToList(), unrelatedDirectory);
        Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(folderInfoTimestamp), snapshot.FolderInfoCandidates.EntriesByPath[folderInfoPath].LastWriteTimeUnixSeconds);
    }

    /// <summary>
    /// 捕捉パスと対象ディレクトリの大小文字が違ってもfolderinfoを採用し、その捕捉時刻を保持する。
    /// </summary>
    [TestMethod]
    public void FolderInfoCandidateEnumerationFromEntries_MatchesTargetDirectoryCaseInsensitively()
    {
        using var scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        string chartDirectory = Path.Combine(rootDirectory, "Pack");
        string folderInfoPath = Path.Combine(chartDirectory, "folderinfo.txt");
        DateTime folderInfoTimestamp = new(2026, 6, 5, 7, 0, 0, DateTimeKind.Utc);
        RootFileEnumerationEntry entry = new(folderInfoPath, folderInfoTimestamp);

        Lr2FolderInfoCandidateSnapshot snapshot = Lr2FolderInfoCandidateEnumerationService.CreateSnapshotFromEntries(
            [entry],
            [chartDirectory.ToUpperInvariant()]);

        CollectionAssert.AreEqual(new[] { Path.GetFullPath(folderInfoPath) }, snapshot.Paths.ToArray());
        Assert.IsTrue(snapshot.DiscoveryComplete);
        Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(folderInfoTimestamp), snapshot.EntriesByPath[Path.GetFullPath(folderInfoPath)].LastWriteTimeUnixSeconds);
    }

    /// <summary>
    /// 時刻未捕捉のパス候補も保持し、物理読取りで時刻を補わない。パスだけの入力を捨てる誤りを検出する。
    /// </summary>
    [TestMethod]
    public void FolderInfoCandidateEnumerationFromSurface_KeepsPathOnlyCandidate()
    {
        using var scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        string chartDirectory = Path.Combine(rootDirectory, "Pack");
        string folderInfoPath = Path.Combine(chartDirectory, "folderinfo.txt");

        Lr2FolderInfoCandidateSnapshot snapshot = Lr2FolderInfoCandidateEnumerationService.CreateSnapshotFromSurface(
            [folderInfoPath],
            [],
            [chartDirectory]);

        CollectionAssert.AreEqual(new[] { Path.GetFullPath(folderInfoPath) }, snapshot.Paths.ToArray());
        Assert.IsFalse(snapshot.EntriesByPath[Path.GetFullPath(folderInfoPath)].LastWriteTimeUnixSeconds.HasValue);
    }

    /// <summary>必須終端投影の待機中は実確定行/cursorとRunningを保ち、実投影・所有取消確認・状態保存の後だけCompletedにする。終了要求に続く投影故障、失敗保存の故障でも先行確定と実要求終端を保持する。</summary>
    /// <remarks>正常譜面の実詳細解析と実モデル/DataPort/Catalog/SQLite/投影を通し、既存投影portの入口gateを別readonly接続から観測する。成功、通常完了側で部分投影・終了要求後に故障する条件、終了取消の捕捉側で投影が故障する条件、投影成功後の所有終了、Failed保存だけを拒否する実triggerを分担する。</remarks>
    [TestMethod]
    [DataRow("completed")]
    [DataRow("projection_failure_with_shutdown")]
    [DataRow("cancel_then_projection_failure")]
    [DataRow("shutdown_after_projection")]
    [DataRow("failed_status_write")]
    public async Task QueueLr2SongDbSync_CompletionWaitsForRequiredProjectionAndOwnerCheckpoint(string outcome)
    {
        using var scope = TestDatabaseScope.Create();
        using var projectionRelease = new ManualResetEventSlim();
        var projectionEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool projectionFails = outcome is "projection_failure_with_shutdown" or "cancel_then_projection_failure" or "failed_status_write";
        bool interrupted = outcome == "shutdown_after_projection";
        string root = Path.Combine(scope.DirectoryPath, "ProjectionFailure");
        Directory.CreateDirectory(root);
        string firstPath = Path.Combine(root, "first.bms");
        string secondPath = Path.Combine(root, "second.bms");
        WriteBasicBms(firstPath, "first committed projection", CreateLr2TooLongResourcePath());
        File.AppendAllText(firstPath, "#PLAYER 1\r\n#PLAYLEVEL 3\r\n#BPM 120\r\n#00111:01\r\n");
        WriteBasicBms(secondPath, "second committed projection", CreateLr2TooLongResourcePath());
        File.AppendAllText(secondPath, "#PLAYER 1\r\n#PLAYLEVEL 3\r\n#BPM 120\r\n#00111:01\r\n");
        ChartFile first = CreateSyncTestFile(firstPath, ChartFileContentReader.ReadSnapshot(firstPath)) with
        { Favorite = 7, Tag = "keep", AddDate = 123, Level = 12, LevelText = "12" };
        ChartFile second = CreateSyncTestFile(secondPath, ChartFileContentReader.ReadSnapshot(secondPath)) with { Level = 12, LevelText = "12" };
        var options = new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true };
        var scheduler = new QueuedUiScheduler();
        var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, null, null, null, null, scheduler,
            optionsSnapshotProvider: () => options, settings: TestBmsFactory.CreateRootSettings([root], testSettings))
        {

            BmsCharts = [first, second]
        };
        BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(library.DatabaseScope, scope.SongDbPath, setup =>
        {
            setup.CreateTable<LR2SongDB.folder>();
            BmsLibraryDbGateway.EnsureChartInfoSchema(setup);
            BmsLibraryDbGateway.EnsureLr2SongDbSyncStatusSchema(setup);
            if (outcome == "failed_status_write")
            {
                setup.Execute("CREATE TRIGGER deny_failed_sync_status BEFORE INSERT ON lr2_song_db_sync_status"
                    + " WHEN NEW.status = 'Failed' BEGIN SELECT RAISE(ABORT, 'forced failure status save'); END;");
            }
            setup.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(first), typeof(LR2SongDB.song));
            setup.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(second), typeof(LR2SongDB.song));
        });
        var initialization = new LibraryFileInitializationResult(InvokeCaptureLr2SongDbSyncScanSurface(
            library, options, [root], CreateCompleteLr2ScanSurface([root], [root], [], [root])));
        BMSLibrary.Lr2SynchronizationOwner composedOwner = library.Lr2Synchronization;
        BMSLibrary.Lr2SynchronizationOwner? owner = null;
        void RequestShutdown()
        {
            library.RequestShutdown("required-compatibility-projection-test");
            (owner ?? throw new InvalidOperationException("The synchronization owner has not been composed."))
                .RequestShutdownCancellation("required-compatibility-projection-test");
        }
        var projection = new GatedCompatibilityProjection(
            ReadSynchronizationDependency<ILr2SynchronizationProjectionPort>(composedOwner, "projection"),
            firstPath, RequestShutdown, outcome, projectionRelease, projectionEntered);
        owner = new BMSLibrary.Lr2SynchronizationOwner(
            ReadSynchronizationDependency<ILr2SynchronizationDataPort>(composedOwner, "data"),
            ReadSynchronizationDependency<ILr2SynchronizationRuntimePort>(composedOwner, "runtime"),
            projection,
            _ => scheduler.Schedule(() => { }),
            composedOwner.OperationAdmission,
            playlistOperationAdmission: composedOwner.PlaylistOperationAdmission);
        Task<Lr2SongDbSyncStatusSnapshot>? operation = null;
        try
        {
            operation = Lr2SongDbSyncRequestCoordinator.QueueAsync(owner, "required_projection_failure", true,
                prepareGeneratedData: null, initializationResult: initialization);
            Task enteredOrFinished = await Task.WhenAny(projectionEntered.Task, operation);
            if (enteredOrFinished == operation)
            {
                await operation;
                Assert.Fail("必須終端投影の入口へ到達する前に同期が終了しました。");
            }
            Assert.IsFalse(operation.IsCompleted);
            Assert.IsTrue(owner.Running);
            Assert.IsTrue(owner.ObservableRunning);
            Assert.AreEqual(0, owner.CompletedVersion);
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Running, owner.GetLr2SongDbSyncStatusSnapshot().Status);
            Assert.AreNotEqual(Lr2SongDbSyncService.CompletedStage, owner.GetLr2SongDbSyncRuntimeSnapshot().Stage);
            Assert.AreEqual(3, owner.GetLr2SongDbSyncStatusSnapshot().ProcessedCursor);
            Assert.IsTrue(owner.OperationAdmission.IsActive);
            Assert.IsTrue(owner.PlaylistOperationAdmission.IsActive);
            Assert.IsTrue(projection.ChartInfoApplied);
            Assert.IsNotNull(library.ResolveChartInfo(first.Sha256, first.Md5));
            Assert.IsNotNull(library.ResolveChartInfo(second.Sha256, second.Md5));
            using (LR2SongDBExtended waitingReadback = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly())
            {
                AssertDurableFacts(waitingReadback, "Running");
            }

            Assert.IsTrue(library.BmsCharts.All(chart => chart.Level == 12 && chart.LevelText == "12"));
            projectionRelease.Set();
            if (projectionFails)
            {
                InvalidOperationException failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                    async () => await operation);
                Assert.AreEqual("required compatibility projection fault", failure.Message);
                StringAssert.Contains(failure.StackTrace ?? string.Empty, nameof(GatedCompatibilityProjection.ThrowCompatibilityProjectionFault));
            }
            else if (interrupted)
            {
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await operation);
            }
            else
            {
                Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed, (await operation).Status);
            }
            Lr2SongDbSyncStatusKind expectedStatus = projectionFails ? Lr2SongDbSyncStatusKind.Failed
                : interrupted ? Lr2SongDbSyncStatusKind.Incomplete : Lr2SongDbSyncStatusKind.Completed;
            Assert.AreEqual(1, projection.CompatibilityCalls, "catchで試行済みの必須投影を再実行しない。");
            Assert.IsFalse(owner.Running);
            Assert.IsFalse(owner.ObservableRunning);
            Assert.AreEqual(expectedStatus == Lr2SongDbSyncStatusKind.Completed ? 1 : 0, owner.CompletedVersion);
            Assert.AreEqual(expectedStatus, owner.GetLr2SongDbSyncStatusSnapshot().Status);
            if (expectedStatus != Lr2SongDbSyncStatusKind.Completed)
            {
                Assert.AreNotEqual(Lr2SongDbSyncService.CompletedStage, owner.GetLr2SongDbSyncRuntimeSnapshot().Stage);
            }
            Assert.AreEqual(3, owner.GetLr2SongDbSyncStatusSnapshot().ProcessedCursor);
            Assert.IsNotNull(library.ResolveChartInfo(first.Sha256, first.Md5));
            Assert.IsNotNull(library.ResolveChartInfo(second.Sha256, second.Md5));
            using (LR2SongDBExtended readback = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly())
            {
                AssertDurableFacts(readback, outcome == "failed_status_write" ? "Running" : expectedStatus.ToString());
                int firstFlags = readback.ExecuteScalar<int>("SELECT lr2_warning_flags FROM maintenance WHERE path = ?;", firstPath);
                int secondFlags = readback.ExecuteScalar<int>("SELECT lr2_warning_flags FROM maintenance WHERE path = ?;", secondPath);
                ChartFile firstLive = library.BmsCharts.Single(file => file.Path == firstPath);
                ChartFile secondLive = library.BmsCharts.Single(file => file.Path == secondPath);
                Assert.AreEqual(3, firstLive.Level);
                Assert.AreEqual("3", firstLive.LevelText);
                Assert.AreEqual(projectionFails ? 12 : 3, secondLive.Level);
                Assert.AreEqual(projectionFails ? "12" : "3", secondLive.LevelText);
                Assert.AreEqual(firstFlags, firstLive.ResourceHealthMaintenanceSnapshot?.Lr2WarningFlags);
                if (projectionFails) { Assert.AreNotEqual(secondFlags, secondLive.ResourceHealthMaintenanceSnapshot?.Lr2WarningFlags); }
                else { Assert.AreEqual(secondFlags, secondLive.ResourceHealthMaintenanceSnapshot?.Lr2WarningFlags); }
            }
            scheduler.Drain();
            Assert.AreEqual(0, scheduler.PendingCount);
            Assert.IsFalse(owner.OperationAdmission.IsActive);
            Assert.IsFalse(owner.PlaylistOperationAdmission.IsActive);
        }
        finally
        {
            projectionRelease.Set();
            try
            {
                if (operation != null)
                {
                    owner.RequestShutdownCancellation("required-projection-cleanup");
                    try { await operation; }
                    catch (InvalidOperationException) { }
                    catch (OperationCanceledException) { }
                }
            }
            finally { scheduler.Drain(); }
        }

        void AssertDurableFacts(LR2SongDBExtended readback, string expectedState)
        {
            LR2SongDBExtended.lr2_song_db_sync_status durable = readback.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.AreEqual(expectedState, durable.status);
            Assert.AreEqual(3, durable.processed_cursor);
            Assert.AreEqual(3, durable.total_count);
            if (expectedState == "Completed") { Assert.IsNotNull(durable.completed_at); }
            else { Assert.IsNull(durable.completed_at); }
            Assert.AreEqual("first committed projection", readback.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", firstPath));
            Assert.AreEqual("second committed projection", readback.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", secondPath));
            Assert.AreEqual(first.Md5, readback.ExecuteScalar<string>("SELECT hash FROM song WHERE path = ?;", firstPath));
            Assert.AreEqual(second.Md5, readback.ExecuteScalar<string>("SELECT hash FROM song WHERE path = ?;", secondPath));
            Assert.AreEqual(2, readback.ExecuteScalar<int>("SELECT COUNT(*) FROM chart_info WHERE sha256 IN (?, ?);", first.Sha256, second.Sha256));
            Assert.IsTrue(readback.ExecuteScalar<int>("SELECT lr2_warning_flags FROM maintenance WHERE path = ?;", firstPath) > 0);
            Assert.IsTrue(readback.ExecuteScalar<int>("SELECT lr2_warning_flags FROM maintenance WHERE path = ?;", secondPath) > 0);
            Assert.AreEqual(7, readback.ExecuteScalar<int>("SELECT favorite FROM song WHERE path = ?;", firstPath));
            Assert.AreEqual("keep", readback.ExecuteScalar<string>("SELECT tag FROM song WHERE path = ?;", firstPath));
            Assert.AreEqual(123, readback.ExecuteScalar<int>("SELECT adddate FROM song WHERE path = ?;", firstPath));
        }
    }

    private sealed class GatedCompatibilityProjection(
        ILr2SynchronizationProjectionPort inner,
        string firstPath,
        Action requestShutdown,
        string outcome,
        ManualResetEventSlim release,
        TaskCompletionSource<bool> entered) : ILr2SynchronizationProjectionPort
    {
        internal int CompatibilityCalls { get; private set; }
        internal bool ChartInfoApplied { get; private set; }
        public void EnsureLr2SongDbSyncChartInfoIndexHydrated(string reason) => inner.EnsureLr2SongDbSyncChartInfoIndexHydrated(reason);
        public Func<ChartFile, BeMusicSeeker.Models.ChartDetails> CreateLr2SongDbSyncChartInfoResolverSnapshot() => inner.CreateLr2SongDbSyncChartInfoResolverSnapshot();
        public HashSet<string> CreateLr2SongDbSyncCurrentChartInfoParseFailureMd5Snapshot(string reason) => inner.CreateLr2SongDbSyncCurrentChartInfoParseFailureMd5Snapshot(reason);
        public void UpsertLr2SongDbSyncChartInfoIndexRows(IReadOnlyList<BeMusicSeeker.Models.ChartDetails> rows)
        {
            inner.UpsertLr2SongDbSyncChartInfoIndexRows(rows);
            ChartInfoApplied = true;
            if (outcome == "cancel_then_projection_failure") { requestShutdown(); }
        }
        public int ApplyLr2SongDbSyncCurrentProjection(IReadOnlyList<Lr2SongDbSyncCurrentFact> facts,
            string reason, bool logSummary, bool dispatchPresentation)
        {
            CompatibilityCalls++;
            entered.TrySetResult(true);
            release.Wait();
            if (outcome is "projection_failure_with_shutdown" or "cancel_then_projection_failure" or "failed_status_write")
            {
                inner.ApplyLr2SongDbSyncCurrentProjection([facts.Single(fact => fact.Chart.Path == firstPath)],
                    reason, logSummary, dispatchPresentation);
                if (outcome == "projection_failure_with_shutdown") { requestShutdown(); }
                return ThrowCompatibilityProjectionFault();
            }
            int applied = inner.ApplyLr2SongDbSyncCurrentProjection(facts, reason, logSummary, dispatchPresentation);
            if (outcome == "shutdown_after_projection") { requestShutdown(); }
            return applied;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int ThrowCompatibilityProjectionFault() => throw new InvalidOperationException("required compatibility projection fault");
        public void DispatchWarningPresentationChanged(string reason, IReadOnlyList<string>? md5s = null) => inner.DispatchWarningPresentationChanged(reason, md5s);
    }

}
