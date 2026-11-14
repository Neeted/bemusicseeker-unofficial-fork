using System.IO;
using System.Text;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryInitializationTestSupport;
namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class BmsLibraryInitializationFileScanTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>
    /// 起動scanの確定をmodel guard解放後に通知し、親受付を保ったままsubscriber失敗を隔離してDBと所持譜面を公開する。
    /// </summary>
    /// <remarks>実ライブラリの起動入口・保存・受付・通知を接続し、下位scan単体では観測できない終端後の利用を確認する。</remarks>
    [TestMethod]
    public void Initialize_StartupPublishesScanAfterLeaseReleaseAndIsolatesTerminalSubscriber()
    {

        using var scope =
            Lr2SongDbSyncTestSupport.TestDatabaseScope.Create();
        ApplicationDatabaseScope databaseScope = scope.DatabaseScope;
        string lr2RootPath = Path.Combine(scope.DirectoryPath, "LR2");
        string songDbPath = scope.SongDbPath;
        string bmsDirectoryPath = Path.Combine(
            ChartInfoComponentTestSupport.FindRepoRoot(),
            "BeMusicSeeker.Tests",
            "TestData",
            "chart_info_real",
            "charts",
            "00");
        string chartPath = Path.Combine(
            bmsDirectoryPath,
            "0011a110d2d54f455a1dbb9a03598aaa1cc092238800ca9b1c1260a8bb78db36.bms");

        string oldNormalExact = ToFolderPath(bmsDirectoryPath.ToLowerInvariant());
        string[] relativeSentinels = [@"Unresolved\Sentinel\", @"C:DriveRelative\", @"\RootRelative\"];
        using (LR2SongDBExtended seed = databaseScope.OpenSongWrite(songDbPath))
        {
            // 実Initializeより先にFull読取りを観測するため、その必須2表をfixtureで準備します。
            seed.CreateTable<LR2SongDB.song>();
            seed.CreateTable<LR2SongDB.folder>();
            seed.InsertOrReplace(new LR2SongDB.folder { path = oldNormalExact, type = 1, date = ToUnixSeconds(Directory.GetLastWriteTimeUtc(bmsDirectoryPath)), adddate = 321 }, typeof(LR2SongDB.folder));
            foreach (string path in relativeSentinels)
            { seed.InsertOrReplace(new LR2SongDB.folder { path = path, title = "Keep relative", parent = "keep-relative-parent", type = 1, date = 123, adddate = 456 }, typeof(LR2SongDB.folder)); }
        }
        SongTableLoadResult fullRead = new BmsLibraryInitializationService().LoadSongTable(new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath),
            new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true }, new TestFileMutationService(), null, failure => failure.Message);
        Assert.IsNotNull(fullRead.FolderTableSnapshot);
        CollectionAssert.AreEquivalent(new[] { oldNormalExact }.Concat(relativeSentinels).ToArray(), fullRead.FolderTableSnapshot.ExistingRowsByPath.Values.Select(row => row.path).ToArray());

        string configDirectoryPath = Path.Combine(lr2RootPath, "LR2files", "Config");
        Directory.CreateDirectory(configDirectoryPath);
        string configPath = Path.Combine(configDirectoryPath, "config.xml");
        string escapedBmsDirectoryPath = System.Security.SecurityElement.Escape(
            ToFolderPath(bmsDirectoryPath));
        File.WriteAllText(
            configPath,
            "<config><system><customfolder>0</customfolder><titleflash>24</titleflash></system><jukebox><path>"
            + escapedBmsDirectoryPath
            + "</path></jukebox></config>",
            Encoding.UTF8);
        LR2Config lr2Config = TestLr2ConfigFactory.Load(configPath);
        var lr2ConfigOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(lr2Config);
        BmsLibraryOptionsSnapshot options = new()
        {
            OperationModeLR2DB = true,
            LR2RootPath = lr2RootPath,
            ScanBmsFilesOnStartup = true,
            UpdateLr2IrRankingCacheOnStartup = false,
            EnableDownloadLr2IrScoreAndDetectUnsent = false,
            UseBeatorajaScoreDb = false,
            EnableReadOptimizedPragmas = false,
            PendingInstallEstimateMaxParallelPackages = 1
        };
        IChartFileScanner chartFileScanner = CapturedChartFileScanner.FromFixture(
            [chartPath],
            new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [bmsDirectoryPath] = []
            },
            [bmsDirectoryPath]);
        var library = new TestBmsLibrary(databaseScope,
            songDbPath,
            getLR2Config: lr2ConfigOwner,
            _lr2ScoreDB: null,
            startupRequiredFileScanReason: null,
            optionsSnapshotProvider: () => options,
            applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
            chartFileScanner: chartFileScanner);
        library.StartupBackgroundTaskScheduler = (_, _, _, _) => false;

        int scanNotificationObserved = 0;
        bool initializationWriterHeldAtNotification = false;
        bool mutationAdmissionAvailable = false;
        int subscriberFailureObserved = 0;
        int handledNotificationVersion = 0;
        System.ComponentModel.PropertyChangedEventHandler scanNotificationSubscriber = (_, args) =>
        {
            if (!string.Equals(
                    args.PropertyName,
                    nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion),
                    StringComparison.Ordinal))
            {
                return;
            }

            NormalLibraryRefreshNotificationBatch notificationBatch =
                library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            if (notificationBatch.LatestVersion > handledNotificationVersion)
            {
                handledNotificationVersion = notificationBatch.LatestVersion;
            }
            if (notificationBatch.LatestVersion <= 0
                || notificationBatch.ResetsPriorNotifications
                || !notificationBatch.HasRefreshNotification
                || Interlocked.Exchange(ref scanNotificationObserved, 1) != 0)
            {
                return;
            }

            initializationWriterHeldAtNotification |= library.IsWriteLockHeldInitializeAll
                || library.IsWriteLockHeldInitializeMin
                || library.IsWriteLockHeldInitializeBMSFiles;
            using LibraryFileMutationLease reentryLease = library.Lr2Synchronization.TryBeginMutation(
                "test_initialize_startup_post_lease_reentry");
            mutationAdmissionAvailable |= reentryLease != null;
            if (Interlocked.Exchange(ref subscriberFailureObserved, 1) == 0)
            {
                throw new InvalidOperationException("forced initialize catalog subscriber failure");
            }
        };
        LibraryFileInitializationResult initializationInput;
        ScoreAdoptionNotification? scoreNotification = null;
        var parent = new StartupLibraryInitializationWorkflowOwner(databaseScope, new BmsDatabaseLoadOptions(true, true, true), new SemaphoreSlim(1, 1));
        using StartupRequiredOperationLease accepted = parent.AcquireRequiredOperation(library.OperationAdmission, library.Lr2Synchronization.PlaylistOperationAdmission);
        library.PropertyChanged += scanNotificationSubscriber;
        int generationStarted = ToUnixSeconds(DateTime.UtcNow);
        try
        {
            initializationInput = library.InitializeStartup([], null, PerformanceInteraction.Start("test_required_startup"), accepted.Capability, leapYearRepairApproval: new(songDbPath), repairNotificationObserver: _ => { }, scoreNotificationObserver: notification => scoreNotification = notification);
        }
        finally
        {
            library.PropertyChanged -= scanNotificationSubscriber;
            accepted.Dispose();
            scoreNotification?.Publication?.Invoke();
        }
        Assert.IsTrue(Volatile.Read(ref scanNotificationObserved) != 0);
        Assert.IsFalse(initializationWriterHeldAtNotification);
        Assert.IsFalse(mutationAdmissionAvailable);
        Assert.IsTrue(Volatile.Read(ref subscriberFailureObserved) != 0);
        Assert.IsTrue(library.BmsCharts.Any(file =>
            string.Equals(file?.Path, chartPath, StringComparison.OrdinalIgnoreCase)));

        Assert.IsNotNull(initializationInput.ScanSurface);
        Assert.IsTrue(initializationInput.CommittedBmsPaths.Contains(chartPath));

        using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
        Assert.IsTrue(verify.Table<LR2SongDB.song>().Any(row =>
            string.Equals(row?.path, chartPath, StringComparison.OrdinalIgnoreCase)));
        LR2SongDB.folder normal = verify.Query<LR2SongDB.folder>("SELECT * FROM folder WHERE path = ? COLLATE BINARY;", ToFolderPath(bmsDirectoryPath)).Single();
        Assert.IsTrue(normal.adddate >= generationStarted && normal.adddate <= ToUnixSeconds(DateTime.UtcNow), "別raw keyの新規folderは起動操作のUTC生成日時を使います。");
        Assert.AreEqual("e2977170", normal.parent);
        Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM folder WHERE path = ? COLLATE BINARY;", oldNormalExact));
        foreach (string path in relativeSentinels)
        {
            Assert.IsFalse(Path.IsPathFullyQualified(path));
            LR2SongDB.folder relative = verify.Query<LR2SongDB.folder>("SELECT * FROM folder WHERE path = ? COLLATE BINARY;", path).Single();
            Assert.AreEqual("Keep relative", relative.title); Assert.AreEqual("keep-relative-parent", relative.parent);
            Assert.AreEqual(123, relative.date); Assert.AreEqual(456, relative.adddate);
            Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM folder WHERE path = ? COLLATE BINARY;", ToFolderPath(path)));
        }

    }

    /// <summary>
    /// 起動scanでbmson解析が失敗しても処理を終端し、後のcatalog変更に起動未走査の警告を追加しない。
    /// </summary>
    /// <remarks>実ライブラリの起動入口・保存・受付・通知を接続し、下位scan単体では観測できない終端後の利用を確認する。</remarks>
    [TestMethod]
    public void Initialize_StartupParseFailureStillOpensCatalogFileMutationAdmission()
    {

        using var scope =
            Lr2SongDbSyncTestSupport.TestDatabaseScope.Create();
        ApplicationDatabaseScope databaseScope = scope.DatabaseScope;
        string chartDirectoryPath = Path.Combine(scope.DirectoryPath, "InvalidBmson");
        Directory.CreateDirectory(chartDirectoryPath);
        string invalidBmsonPath = Path.Combine(chartDirectoryPath, "invalid.bmson");
        File.WriteAllText(invalidBmsonPath, "{\"version\":\"1.0.0\",\"info\":");
        BmsLibraryOptionsSnapshot options = new()
        {
            OperationModeLR2DB = false,
            ScanBmsFilesOnStartup = true,
            UpdateLr2IrRankingCacheOnStartup = false,
            EnableDownloadLr2IrScoreAndDetectUnsent = false,
            UseBeatorajaScoreDb = false,
            EnableReadOptimizedPragmas = false,
            PendingInstallEstimateMaxParallelPackages = 1
        };
        IChartFileScanner chartFileScanner = CapturedChartFileScanner.FromFixture(
            [invalidBmsonPath],
            new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [chartDirectoryPath] = []
            },
            [chartDirectoryPath]);
        var dialogService = new RecordingDialogService();
        var library = new TestBmsLibrary(databaseScope,
            scope.SongDbPath,
            getLR2Config: null,
            _lr2ScoreDB: null,
            startupRequiredFileScanReason: null,
            optionsSnapshotProvider: () => options,
            applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
            chartFileScanner: chartFileScanner,
            dialogService: dialogService, settings: TestBmsFactory.CreateRootSettings([chartDirectoryPath]))
        {

            StartupBackgroundTaskScheduler = (_, _, _, _) => false
        };

        try
        {
            library.Initialize(null, null, BMSLibrary.LibraryInitializeMode.Startup);

            Assert.AreEqual(0, library.BmsonCharts.Count);
            int dialogCountBeforeMutation = dialogService.Calls.Count;
            int postedCountBeforeMutation = dialogService.Posted.Count;
            library.RenameBMSFilesExtensions([], ".invalid", new LibraryOperationNotices());
            Assert.AreEqual(dialogCountBeforeMutation, dialogService.Calls.Count);
            Assert.AreEqual(postedCountBeforeMutation, dialogService.Posted.Count);
        }
        finally
        {
            library.RequestShutdown("parse-failure-convergence-test");
        }
    }

    /// <summary>
    /// 起動scanを無効にした後もplaylistの受付は利用でき、catalogの物理変更だけは設定由来の未走査警告で拒否する。
    /// </summary>
    /// <remarks>実ライブラリの起動入口・保存・受付・通知を接続し、下位scan単体では観測できない終端後の利用を確認する。</remarks>
    [TestMethod]
    public void Initialize_StartupWithoutFileScanKeepsPlaylistLeaseAvailableAndCollectsSettingWarningForCatalogMutation()
    {

        using var scope =
            Lr2SongDbSyncTestSupport.TestDatabaseScope.Create();
        ApplicationDatabaseScope databaseScope = scope.DatabaseScope;
        BmsLibraryOptionsSnapshot options = new()
        {
            OperationModeLR2DB = false,
            ScanBmsFilesOnStartup = false,
            UpdateLr2IrRankingCacheOnStartup = false,
            EnableDownloadLr2IrScoreAndDetectUnsent = false,
            UseBeatorajaScoreDb = false,
            EnableReadOptimizedPragmas = false,
            PendingInstallEstimateMaxParallelPackages = 1
        };
        var dialogService = new RecordingDialogService();
        var library = new TestBmsLibrary(databaseScope,
            scope.SongDbPath,
            getLR2Config: null,
            _lr2ScoreDB: null,
            startupRequiredFileScanReason: null,
            optionsSnapshotProvider: () => options,
            applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
            dialogService: dialogService)
        {
            StartupBackgroundTaskScheduler = (_, _, _, _) => false
        };

        try
        {
            library.Initialize(null, null, BMSLibrary.LibraryInitializeMode.Startup);

            using (LibraryFileMutationLease mutationLease = library.Lr2Synchronization.TryBeginMutation(
                "test_playlist_owned_mutation_after_scan_skip"))
            {
                Assert.IsNotNull(mutationLease);
            }

            int dialogCountBeforeCatalogMutation = dialogService.Calls.Count;
            int postedCountBeforeCatalogMutation = dialogService.Posted.Count;
            var notices = new LibraryOperationNotices();
            library.RenameBMSFilesExtensions([], ".invalid", notices);

            Assert.AreEqual(dialogCountBeforeCatalogMutation, dialogService.Calls.Count, "未収束の警告を同期の表示で要求しない。");
            Assert.AreEqual(postedCountBeforeCatalogMutation, dialogService.Posted.Count, "未収束の警告は、操作の収集先へ加え、モデルの窓口へは投函しない。");
            Assert.AreEqual(
                BeMusicSeeker.Properties.Resources.Warn_CatalogFileMutationRequiresStartupScan,
                notices.Snapshot().Single().MessageBoxText);
        }
        finally
        {
            library.RequestShutdown("playlist-mutation-after-scan-skip-test");
        }
    }

}
