using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using Microsoft.VisualBasic.FileIO;
using static BeMusicSeeker.TestSupport.Components.InstallServiceComponentTestSupport;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.Tests;

public sealed partial class BmsLibraryPackageInstallServiceTests
{
    /// <summary>
    /// 管理一時フォルダの保留を除くと実フォルダも削除し、未収束を理由とする不要な警告や管理入力の残存を検出する。
    /// </summary>
    /// <remarks>実ライブラリの保存・排他・通知経路に接続するため独立したSQLite DBと実フォルダを用いる。</remarks>
    [TestMethod]
    // 独立compositionを同一processで作るfixtureは共通session markerの初期公開を競合させるため、作成・回収を非並列で所有します。本番は同一Lで直列となります。
    [DoNotParallelize]
    public async Task RemovePendingPackages_DeletesManagedTemporaryPackageSource()
    {
        var databaseScope = new ApplicationDatabaseScope();

        await WithTemporarySongDbAsync(async (songDbPath, tempRootPath) =>
        {
            string packageDirectoryPath = TempDirectoryPublisher.Get("pending-remove-test");
            File.WriteAllText(Path.Combine(packageDirectoryPath, "chart.bms"), "#TITLE test");
            var pendingPackage = new ChartPackage
            {
                path = packageDirectoryPath
            };
            var dialogService = new RecordingDialogService();
            var library = new TestBmsLibrary(databaseScope,
                songDbPath,
                null,
                null,
                new RealFileMutationService(),
                dialogService)
            {
                ChartPackagesPending = CreatePackageCollection([pendingPackage])
            };
            library.ResetCatalogPathConvergence(CatalogPathConvergenceBlockReason.StartupFileScanDisabled);

            try
            {
                await library.RemovePendingPackagesAsync([pendingPackage], () => Task.CompletedTask);
                Assert.AreEqual(0, library.ChartPackagesPending.Count);
                Assert.IsFalse(Directory.Exists(packageDirectoryPath));
                Assert.AreEqual(0, dialogService.Messages.Count);
            }
            finally { library.RequestShutdown("pending-remove-test"); TempDirectoryPublisher.TryDeleteManagedPath(packageDirectoryPath); }
        });
    }

    /// <summary>
    /// カタログ未収束でも保留全解除を受け付け、管理入力だけを回収して借用・導入済み実体を保ち、警告と所有範囲の誤りを検出する。
    /// </summary>
    /// <remarks>実ライブラリの保存・排他・通知経路に接続するため独立したSQLite DBと実フォルダを用いる。</remarks>
    [TestMethod]
    // 同一processの独立compositionが共通session markerを初期公開するfixture都合で、固有root回収まで非並列とします。本番の同一Lの受付は変更しません。
    [DoNotParallelize]
    public async Task RemovePendingPackagesAll_UnconvergedCatalogStillClearsPendingWithoutWarning()
    {
        var databaseScope = new ApplicationDatabaseScope();

        await WithTemporarySongDbAsync(async (songDbPath, tempRootPath) =>
        {
            string managed = TempDirectoryPublisher.Get("pending-clear-mixed");
            string borrowed = Path.Combine(tempRootPath, "borrowed.bms");
            string installed = Path.Combine(tempRootPath, "installed.bms");
            File.WriteAllText(Path.Combine(managed, "managed.bms"), "#TITLE managed");
            File.WriteAllText(borrowed, "#TITLE borrowed");
            File.WriteAllText(installed, "#TITLE installed");
            var dialogService = new RecordingDialogService();
            var library = new TestBmsLibrary(databaseScope,
                songDbPath,
                null,
                null,
                new RealFileMutationService(),
                dialogService)
            {
                ChartPackagesPending = CreatePackageCollection([
                    new ChartPackage { path = managed },
                    new ChartPackage { path = borrowed }]),
                ChartPackagesInstalled = CreatePackageCollection([new ChartPackage { path = installed }])
            };
            library.ResetCatalogPathConvergence(CatalogPathConvergenceBlockReason.StartupFileScanDisabled);
            try
            {
                await library.RemovePendingPackagesAllAsync(() => Task.CompletedTask);
                Assert.AreEqual(0, library.ChartPackagesPending.Count);
                Assert.IsFalse(Directory.Exists(managed));
                Assert.AreEqual("#TITLE borrowed", File.ReadAllText(borrowed));
                Assert.AreEqual("#TITLE installed", File.ReadAllText(installed));
                Assert.AreEqual(0, dialogService.Messages.Count);
            }
            finally { library.RequestShutdown("pending-clear-test"); TempDirectoryPublisher.TryDeleteManagedPath(managed); }
        });
    }

    /// <summary>
    /// 利用者所有フォルダは保留から除いても実フォルダを保ち、管理一時入力と混同した削除を検出する。
    /// </summary>
    /// <remarks>実ライブラリの保存・排他・通知経路に接続するため独立したSQLite DBと実フォルダを用いる。</remarks>
    [TestMethod]
    public async Task RemovePendingPackages_DoesNotDeleteUserOwnedPackageSource()
    {
        var databaseScope = new ApplicationDatabaseScope();

        await WithTemporarySongDbAsync(async (songDbPath, tempRootPath) =>
        {
            using var caseWorkspace1 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
            string packageDirectoryPath = Path.Combine(caseWorkspace1.RootDirectory, "BeMusicSeeker", "session-manual-user-package-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(packageDirectoryPath);
            File.WriteAllText(Path.Combine(packageDirectoryPath, "chart.bms"), "#TITLE test");
            try
            {
                var pendingPackage = new ChartPackage
                {
                    path = packageDirectoryPath
                };
                var library = new TestBmsLibrary(databaseScope, songDbPath)
                {
                    ChartPackagesPending = CreatePackageCollection([pendingPackage])
                };

                await library.RemovePendingPackagesAsync([pendingPackage], () => Task.CompletedTask);

                Assert.AreEqual(0, library.ChartPackagesPending.Count);
                Assert.IsTrue(Directory.Exists(packageDirectoryPath));
            }
            finally
            {
                if (Directory.Exists(packageDirectoryPath))
                {
                    Directory.Delete(packageDirectoryPath, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// 保留拡張子の実変更後に排他を解放して一度通知し、通知先例外で成功を戻す誤りと通知中の排他保持を検出する。
    /// </summary>
    /// <remarks>実ライブラリの保存・排他・通知経路に接続するため独立したSQLite DBと実フォルダを用いる。</remarks>
    [TestMethod]
    public void RenamePendingBmsFormatChartFileExtensions_PublishesAfterLeaseReleaseAndIsolatesSubscriberFailure()
    {
        var databaseScope = new ApplicationDatabaseScope();

        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "pending-invalid-extension");
            string sourceChartPath = CreateBmsFile(sourceDirectoryPath, "chart.bms", "#TITLE Pending rename");
            string destinationChartPath = Path.Combine(sourceDirectoryPath, "chart.bme");
            ChartFile sourceChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(sourceChartPath));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([sourceChart]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            var library = new TestBmsLibrary(databaseScope,
                songDbPath,
                null,
                null,
                new RealFileMutationService(),
                new RecordingDialogService());
            library.ChartPackagesPending = CreatePackageCollection([pendingPackage]);

            int publicationCount = 0;
            bool publicationObservedFilesystem = false;
            bool publicationObservedReleasedLease = false;
            library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(BMSLibrary.ChartPackagesPending))
                {
                    return;
                }
                publicationCount++;
                publicationObservedFilesystem = !File.Exists(sourceChartPath)
                    && File.Exists(destinationChartPath)
                    && library.ChartPackagesPending.Count == 0;
                using LibraryFileMutationLease probe = library.Lr2Synchronization.TryBeginMutation(
                    "pending_invalid_extension_publication_probe");
                publicationObservedReleasedLease = probe != null;
                throw new InvalidOperationException("pending collection subscriber failed");
            };

            library.RenamePendingBmsFormatChartFileExtensions(
                [(sourceChart)],
                ".bme");

            Assert.AreEqual(1, publicationCount);
            Assert.IsTrue(publicationObservedFilesystem);
            Assert.IsTrue(publicationObservedReleasedLease);
            Assert.AreEqual(0, library.ChartPackagesPending.Count);
        });
    }

    /// <summary>
    /// 保留DB反映が失敗しても先行の物理変更失敗の通知を回収し、後続の物理成功と元DB失敗を保持して通知や成功対象の欠落を検出する。
    /// </summary>
    /// <remarks>実ライブラリの保存・排他・通知経路に接続するため独立したSQLite DBと実フォルダを用いる。</remarks>
    [TestMethod]
    public void RenamePendingBmsFormatChartFileExtensions_FlushesEarlierEffectWhenPackageApplyFails()
    {
        var databaseScope = new ApplicationDatabaseScope();

        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string firstDirectoryPath = Path.Combine(tempRootPath, "pending-invalid-extension-first");
            string secondDirectoryPath = Path.Combine(tempRootPath, "pending-invalid-extension-second");
            string thirdDirectoryPath = Path.Combine(tempRootPath, "pending-invalid-extension-third");
            string firstChartPath = CreateBmsFile(firstDirectoryPath, "first.bms", "#TITLE First failure");
            string secondChartPath = CreateBmsFile(secondDirectoryPath, "second.bms", "#TITLE Second failure");
            string thirdChartPath = CreateBmsFile(thirdDirectoryPath, "third.bms", "#TITLE Durable rename");
            string thirdDestinationChartPath = Path.Combine(thirdDirectoryPath, "third.bme");
            ChartFile firstChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(firstChartPath));
            ChartFile secondChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(secondChartPath));
            ChartFile thirdChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(thirdChartPath));
            ChartPackage firstPackage = ChartPackageTestExtensions.CreatePackage([firstChart]);
            firstPackage.path = firstDirectoryPath;
            firstPackage.delete_parent = false;
            ChartPackage secondPackage = ChartPackageTestExtensions.CreatePackage([secondChart]);
            secondPackage.path = secondDirectoryPath;
            secondPackage.delete_parent = false;
            ChartPackage thirdPackage = ChartPackageTestExtensions.CreatePackage([thirdChart]);
            thirdPackage.path = thirdDirectoryPath;
            thirdPackage.delete_parent = false;
            var dialogService = new ThrowOnFirstPendingRenameDialogService();
            var library = new TestBmsLibrary(databaseScope,
                songDbPath,
                null,
                null,
                new FailFirstMoveFileMutationService(2),
                dialogService);
            library.ChartPackagesPending = CreatePackageCollection([firstPackage, secondPackage, thirdPackage]);

            using (LR2SongDBExtended songDb = databaseScope.OpenSongWrite(songDbPath))
            {
                songDb.Execute("DROP TABLE install");
            }

            SQLite.SQLiteException primaryFailure = Assert.ThrowsExactly<SQLite.SQLiteException>(() =>
                library.RenamePendingBmsFormatChartFileExtensions(
                    [
                        (firstChart),
                        (secondChart),
                        (thirdChart)
                    ],
                    ".bme"));
            StringAssert.Contains(primaryFailure.Message, "install");
            Assert.AreEqual(2, dialogService.CallCount);
            Assert.IsTrue(dialogService.LaterFailureWasObserved);
            Assert.IsTrue(File.Exists(firstChartPath));
            Assert.IsTrue(File.Exists(secondChartPath));
            Assert.IsTrue(File.Exists(thirdDestinationChartPath));
            Assert.AreEqual(3, library.ChartPackagesPending.Count);
        });
    }

    /// <summary>
    /// 通常譜面の拡張子変更で先行の物理失敗と後続のDB失敗が起きても、排他解放後に先行失敗を知らせ、後続の物理成功を失う誤りを検出する。
    /// </summary>
    /// <remarks>実ライブラリの保存・排他・通知経路に接続するため独立したSQLite DBと実フォルダを用いる。</remarks>
    [TestMethod]
    public void RenameBMSFilesExtensions_FlushesEarlierFailureAfterCatalogApplyFailure()
    {
        var databaseScope = new ApplicationDatabaseScope();

        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "normal-invalid-extension");
            string firstChartPath = CreateBmsFile(sourceDirectoryPath, "first.bms", "#TITLE First failure");
            string secondChartPath = CreateBmsFile(sourceDirectoryPath, "second.bms", "#TITLE Durable rename");
            string secondDestinationChartPath = Path.Combine(sourceDirectoryPath, "second.bme");
            ChartFile firstChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(firstChartPath));
            ChartFile secondChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(secondChartPath));
            var dialogService = new BmsLibraryInitializationTestSupport.RecordingDialogService();
            var library = new TestBmsLibrary(databaseScope,
                songDbPath,
                null,
                null,
                new FailFirstMoveFileMutationService(1),
                dialogService);
            OwnedChartCollectionTestSupport.SetLibraryFilesWithoutNotification(library, [firstChart, secondChart]);

            using (LR2SongDBExtended songDb = databaseScope.OpenSongWrite(songDbPath))
            {
                songDb.Execute("DROP TABLE song");
            }

            bool failureDialogObservedReleasedLease = false;
            dialogService.OnShow = _ =>
            {
                using LibraryFileMutationLease probe = library.Lr2Synchronization.TryBeginMutation(
                    "normal_invalid_extension_failure_notification_probe");
                failureDialogObservedReleasedLease = probe != null;
            };

            SQLite.SQLiteException primaryFailure = Assert.ThrowsExactly<SQLite.SQLiteException>(() =>
                library.RenameBMSFilesExtensions(
                    [
                        (firstChart),
                        (secondChart)
                    ],
                    ".bme",
                    unregister: false));
            StringAssert.Contains(primaryFailure.Message, "song");
            Assert.AreEqual(1, dialogService.Calls.Count);
            Assert.IsTrue(failureDialogObservedReleasedLease);
            Assert.IsTrue(File.Exists(firstChartPath));
            Assert.IsFalse(File.Exists(secondChartPath));
            Assert.IsTrue(File.Exists(secondDestinationChartPath));
        });
    }

    /// <summary>
    /// 拡張子変更のDB確定後にLR2同期が失敗したら例外を伝えつつFS・保存行・所持パスの確定を保ち、確定済み変更の巻戻しを検出する。
    /// </summary>
    /// <remarks>実ライブラリの保存・排他・通知経路に接続するため独立したSQLite DBと実フォルダを用いる。</remarks>
    [TestMethod]
    public void RenameBMSFilesExtensions_PropagatesDurableAfterCommitFailure()
    {
        var databaseScope = new ApplicationDatabaseScope();

        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "normal-invalid-extension-durable-failure");
            string lr2RootPath = Path.Combine(tempRootPath, "LR2beta3");
            string sourceChartPath = CreateBmsFile(sourceDirectoryPath, "chart.bms", "#TITLE Durable failure");
            string destinationChartPath = Path.Combine(sourceDirectoryPath, "chart.bme");
            ChartFile chart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(sourceChartPath));
            LR2Config lr2Config = TestLr2ConfigFactory.CreateLr2Config(lr2RootPath, sourceDirectoryPath);
            var lr2ConfigOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(lr2Config);
            var library = new TestBmsLibrary(databaseScope,
                songDbPath,
                lr2ConfigOwner,
                null,
                new RealFileMutationService(),
                new RecordingDialogService(),
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true
                });
            library.FixtureSettings.StandaloneBmsRootPaths = StandaloneBmsRootPathSettings.Serialize([sourceDirectoryPath]);
            OwnedChartCollectionTestSupport.SetLibraryFilesWithoutNotification(library, [chart]);

            using (LR2SongDBExtended songDb = databaseScope.OpenSongWrite(songDbPath))
            {
                songDb.Execute("DROP TABLE folder");
                songDb.Execute("CREATE TABLE folder (x TEXT)");
                songDb.Commit();
            }

            SQLite.SQLiteException durableFailure = Assert.ThrowsExactly<SQLite.SQLiteException>(() =>
                library.RenameBMSFilesExtensions(
                    [(chart)],
                    ".bme",
                    unregister: false));

            Assert.IsNotNull(durableFailure);
            Assert.IsFalse(File.Exists(sourceChartPath));
            Assert.IsTrue(File.Exists(destinationChartPath));
            Assert.AreEqual(0, library.BmsCharts.Count(file => string.Equals(file.Path, sourceChartPath, StringComparison.OrdinalIgnoreCase)));
            Assert.AreEqual(1, library.BmsCharts.Count(file => string.Equals(file.Path, destinationChartPath, StringComparison.OrdinalIgnoreCase)));
            using LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", sourceChartPath));
            Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", destinationChartPath));
        });
    }

    /// <summary>
    /// 拡張子変更に成功した選択譜面だけを登録解除し、失敗した譜面と別所有者の同一MD5を保ち、全件索引再構築やハッシュの過剰削除を検出する。
    /// </summary>
    /// <remarks>実ライブラリの保存・排他・通知経路に接続するため独立したSQLite DBと実フォルダを用いる。</remarks>
    [TestMethod]
    public void RenameBMSFilesExtensions_UnregistersOnlySuccessfulChartsAndPreservesHashOwner()
    {
        var databaseScope = new ApplicationDatabaseScope();

        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string failedDirectoryPath = Path.Combine(tempRootPath, "normal-invalid-extension-failed");
            string selectedSharedDirectoryPath = Path.Combine(tempRootPath, "normal-invalid-extension-selected-shared");
            string survivingSharedDirectoryPath = Path.Combine(tempRootPath, "normal-invalid-extension-surviving-shared");
            string lastOwnerDirectoryPath = Path.Combine(tempRootPath, "normal-invalid-extension-last-owner");
            string failedSourcePath = CreateBmsFile(failedDirectoryPath, "failed.bms", "#TITLE Failed target");
            string selectedSharedSourcePath = CreateBmsFile(
                selectedSharedDirectoryPath,
                "selected-shared.bms",
                "#TITLE Shared owner");
            string survivingSharedSourcePath = Path.Combine(survivingSharedDirectoryPath, "surviving-shared.bms");
            Directory.CreateDirectory(survivingSharedDirectoryPath);
            File.Copy(selectedSharedSourcePath, survivingSharedSourcePath);
            string lastOwnerSourcePath = CreateBmsFile(
                lastOwnerDirectoryPath,
                "last-owner.bms",
                "#TITLE Last owner");
            string failedDestinationPath = Path.Combine(failedDirectoryPath, "failed.bme");
            string selectedSharedDestinationPath = Path.Combine(selectedSharedDirectoryPath, "selected-shared.bme");
            string lastOwnerDestinationPath = Path.Combine(lastOwnerDirectoryPath, "last-owner.bme");
            ChartFile failed = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(failedSourcePath));
            ChartFile selectedShared = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(selectedSharedSourcePath));
            ChartFile survivingShared = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(survivingSharedSourcePath));
            ChartFile lastOwner = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(lastOwnerSourcePath));
            Assert.AreEqual(selectedShared.Md5, survivingShared.Md5);

            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(databaseScope, songDbPath, seedSongDb =>
            {
                foreach (ChartFile file in new[] { failed, selectedShared, survivingShared, lastOwner })
                {
                    seedSongDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                }
            });

            var dialogService = new BmsLibraryInitializationTestSupport.RecordingDialogService();
            var library = new TestBmsLibrary(databaseScope,
                songDbPath,
                null,
                null,
                new FailFirstMoveFileMutationService(1),
                dialogService);
            OwnedChartCollectionTestSupport.SetLibraryFilesWithoutNotification(
                library,
                [failed, selectedShared, survivingShared, lastOwner]);

            BMSLibrary.InstalledPrimaryHashWarmupResult initialPrimary =
                library.WarmInstalledPrimaryHashLookup("normal_invalid_extension_unregister_warmup");
            Assert.IsFalse(initialPrimary.FullDirectoryLookupInitialized);
            OwnedChartHashIndexVersionedSnapshot initialHash = library.GetOwnedChartHashIndexSnapshot();
            InstalledChartLookupIndexSnapshot initialInstalled =
                OwnedChartCollectionTestSupport.InvokeCreateInstalledChartLookupSnapshot(library);
            PlaylistLibraryResolveIndexSnapshot initialPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool initialPlaylistCacheHit,
                out int initialPlaylistStaleRetries);
            Assert.IsFalse(initialPlaylistCacheHit);
            Assert.AreEqual(0, initialPlaylistStaleRetries);
            Assert.IsTrue(initialHash.ContainsMd5(failed.Md5));
            Assert.IsTrue(initialHash.ContainsMd5(selectedShared.Md5));
            Assert.IsTrue(initialHash.ContainsMd5(lastOwner.Md5));
            Assert.IsTrue(initialInstalled.ContainsPrimaryHash(failed.Md5));
            Assert.IsTrue(initialInstalled.ContainsPrimaryHash(selectedShared.Md5));
            Assert.IsTrue(initialInstalled.ContainsPrimaryHash(lastOwner.Md5));
            CollectionAssert.AreEquivalent(
                new[] { survivingSharedDirectoryPath, selectedSharedDirectoryPath },
                initialInstalled.GetDistinctDirectoriesByPrimaryHash(selectedShared.Md5).ToArray());
            Assert.IsTrue(initialPlaylist.ContainsCandidate(ChartFileKind.Bms, failedSourcePath));
            Assert.IsTrue(initialPlaylist.ContainsCandidate(ChartFileKind.Bms, selectedSharedSourcePath));
            Assert.IsTrue(initialPlaylist.ContainsCandidate(ChartFileKind.Bms, survivingSharedSourcePath));
            Assert.IsTrue(initialPlaylist.ContainsCandidate(ChartFileKind.Bms, lastOwnerSourcePath));

            List<string> hashWork = [];
            List<string> playlistWork = [];
            List<string> installedWork = [];
            library.OwnedChartHashIndexStoreWorkObserver = hashWork.Add;
            library.PlaylistLibraryResolveIndexStoreWorkObserver = playlistWork.Add;
            library.InstalledChartLookupStoreWorkObserver = installedWork.Add;

            library.RenameBMSFilesExtensions(
                [
                    (failed),
                    (selectedShared),
                    (lastOwner)
                ],
                ".bme",
                unregister: true);

            Assert.AreEqual(1, dialogService.Calls.Count);
            Assert.IsTrue(File.Exists(failedSourcePath));
            Assert.IsFalse(File.Exists(failedDestinationPath));
            Assert.IsFalse(File.Exists(selectedSharedSourcePath));
            Assert.IsTrue(File.Exists(selectedSharedDestinationPath));
            Assert.IsFalse(File.Exists(lastOwnerSourcePath));
            Assert.IsTrue(File.Exists(lastOwnerDestinationPath));
            Assert.IsTrue(File.Exists(survivingSharedSourcePath));

            Assert.IsTrue(library.BmsCharts.Any(file =>
                string.Equals(file.Path, failedSourcePath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(library.BmsCharts.Any(file =>
                string.Equals(file.Path, survivingSharedSourcePath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(library.BmsCharts.Any(file =>
                string.Equals(file.Path, selectedSharedSourcePath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(library.BmsCharts.Any(file =>
                string.Equals(file.Path, lastOwnerSourcePath, StringComparison.OrdinalIgnoreCase)));

            using (LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", failedSourcePath));
                Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", survivingSharedSourcePath));
                Assert.AreEqual(0, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", selectedSharedSourcePath));
                Assert.AreEqual(0, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", lastOwnerSourcePath));
            }

            OwnedChartHashIndexVersionedSnapshot updatedHash = library.GetOwnedChartHashIndexSnapshot();
            OwnedChartHashIndexVersionedSnapshot cachedHash = library.GetOwnedChartHashIndexSnapshot();
            InstalledChartLookupIndexSnapshot updatedInstalled =
                OwnedChartCollectionTestSupport.InvokeCreateInstalledChartLookupSnapshot(library);
            InstalledChartLookupIndexSnapshot cachedInstalled =
                OwnedChartCollectionTestSupport.InvokeCreateInstalledChartLookupSnapshot(library);
            BMSLibrary.InstalledPrimaryHashWarmupResult updatedPrimary =
                library.WarmInstalledPrimaryHashLookup("normal_invalid_extension_unregister_warmup");
            PlaylistLibraryResolveIndexSnapshot updatedPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool updatedPlaylistCacheHit,
                out int updatedPlaylistStaleRetries);
            PlaylistLibraryResolveIndexSnapshot cachedPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool cachedPlaylistCacheHit,
                out int cachedPlaylistStaleRetries);

            Assert.AreSame(updatedHash, cachedHash);
            Assert.AreSame(updatedInstalled, cachedInstalled);
            Assert.AreEqual("cached", updatedPrimary.Status);
            Assert.AreEqual(0L, updatedPrimary.BuildMs);
            Assert.IsTrue(updatedPlaylistCacheHit, string.Join(";", playlistWork));
            Assert.AreEqual(0, updatedPlaylistStaleRetries);
            Assert.IsTrue(cachedPlaylistCacheHit);
            Assert.AreEqual(0, cachedPlaylistStaleRetries);
            Assert.AreSame(updatedPlaylist, cachedPlaylist);
            Assert.IsTrue(updatedHash.ContainsMd5(failed.Md5));
            Assert.IsTrue(updatedHash.ContainsMd5(selectedShared.Md5));
            Assert.IsFalse(updatedHash.ContainsMd5(lastOwner.Md5));
            Assert.IsTrue(updatedInstalled.ContainsPrimaryHash(failed.Md5));
            Assert.IsTrue(updatedInstalled.ContainsPrimaryHash(selectedShared.Md5));
            Assert.IsFalse(updatedInstalled.ContainsPrimaryHash(lastOwner.Md5));
            CollectionAssert.AreEquivalent(
                new[] { survivingSharedDirectoryPath },
                updatedInstalled.GetDistinctDirectoriesByPrimaryHash(selectedShared.Md5).ToArray());
            Assert.AreEqual(0, updatedInstalled.GetDistinctDirectoriesByPrimaryHash(lastOwner.Md5).Count);
            Assert.IsTrue(updatedPlaylist.ContainsCandidate(ChartFileKind.Bms, failedSourcePath));
            Assert.IsFalse(updatedPlaylist.ContainsCandidate(ChartFileKind.Bms, selectedSharedSourcePath));
            Assert.IsTrue(updatedPlaylist.ContainsCandidate(ChartFileKind.Bms, survivingSharedSourcePath));
            Assert.IsFalse(updatedPlaylist.ContainsCandidate(ChartFileKind.Bms, lastOwnerSourcePath));
            Assert.IsTrue(initialHash.ContainsMd5(lastOwner.Md5));
            Assert.IsTrue(initialInstalled.ContainsPrimaryHash(lastOwner.Md5));
            Assert.IsTrue(initialPlaylist.ContainsCandidate(ChartFileKind.Bms, lastOwnerSourcePath));
            Assert.AreEqual(0, hashWork.Count(operation => operation == "owned_hash_source_enumeration"));
            Assert.AreEqual(
                0,
                playlistWork.Count(operation => operation == "playlist_resolve_source_enumeration"
                    || operation == "playlist_resolve_full_root_enumeration"));
            Assert.IsTrue(
                installedWork.Count(operation => operation == "installed_primary_hash_count_update") > 0,
                "通常拡張子修正の登録解除でinstalled lookup差分更新を観測できませんでした。");
        });
    }

    /// <summary>
    /// 実Bmsonの所持ハッシュをライブラリ入口から照合し、所持のみの保留パッケージが結果から欠落する誤りを検出する。
    /// </summary>
    /// <remarks>実ライブラリの保存・排他・通知経路に接続するため独立したSQLite DBと実フォルダを用いる。</remarks>
    [TestMethod]
    public void GetPendingPackagesContainingOnlyInstalledCharts_MatchesInstalledBmsonByChartEntryHash()
    {
        var databaseScope = new ApplicationDatabaseScope();

        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string packageDirectoryPath = Path.Combine(tempRootPath, "package");
            Directory.CreateDirectory(packageDirectoryPath);
            string bmsonPath = Path.Combine(packageDirectoryPath, "chart.bmson");
            File.WriteAllText(bmsonPath, "{\"info\":{\"title\":\"Song\",\"mode_hint\":\"beat-7k\"},\"sound_channels\":[]}");
            ChartFile installedBmson = ChartTestValues.ReadBmson(bmsonPath);
            var pendingPackage = new ChartPackage
            {
                path = packageDirectoryPath
            };
            var library = new TestBmsLibrary(databaseScope, songDbPath)
            {
                BmsonCharts = [installedBmson],
                ChartPackagesPending = CreatePackageCollection([pendingPackage])
            };

            List<ChartPackage> result = library.GetPendingPackagesContainingOnlyInstalledCharts();

            Assert.AreEqual(1, result.Count);
            Assert.AreSame(pendingPackage, result[0]);
        });
    }

    /// <summary>
    /// BMS所持集合がなくてもBmsonの資源のみを実導入し、既存識別を保って排他解放後に通知することで、種類依存の欠落と通知中のロック保持を検出する。
    /// </summary>
    /// <remarks>実ライブラリの保存・排他・通知経路に接続するため独立したSQLite DBと実フォルダを用いる。</remarks>
    [TestMethod]
    public void InstallPendingPackagesToEstimatedDestinations_ResourceOnlyBmsonWorksWithoutBmsFiles()
    {
        var databaseScope = new ApplicationDatabaseScope();

        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string destinationDirectoryPath = Path.Combine(tempRootPath, "installed");
            string pendingDirectoryPath = Path.Combine(tempRootPath, "pending");
            Directory.CreateDirectory(destinationDirectoryPath);
            Directory.CreateDirectory(pendingDirectoryPath);
            string installedBmsonPath = Path.Combine(destinationDirectoryPath, "chart.bmson");
            string pendingBmsonPath = Path.Combine(pendingDirectoryPath, "chart.bmson");
            string pendingResourcePath = Path.Combine(pendingDirectoryPath, "sound.wav");
            File.WriteAllText(installedBmsonPath, "{}");
            File.WriteAllText(pendingBmsonPath, "{}");
            File.WriteAllText(pendingResourcePath, "resource");
            ChartFile installedBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = installedBmsonPath,
                Folder = destinationDirectoryPath,
                RawTitle = "Installed",
                Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                Sha256 = new string('b', 64)
            };
            ChartFile pendingBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = pendingBmsonPath,
                Folder = pendingDirectoryPath,
                RawTitle = "Pending",
                Md5 = installedBmson.Md5,
                Sha256 = installedBmson.Sha256
            };
            using (LR2SongDBExtended seedSongDb = databaseScope.OpenSongWrite(songDbPath))
            {
                seedSongDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(installedBmson), typeof(LR2SongDBExtended.bmson_song));
            }
            var pendingEntry = PackageChartEntry.FromChart((pendingBmson));
            pendingEntry.ApplyInstallDestinationMetadata(destinationDirectoryPath, "Installed", "Artist");
            var pendingPackage = ChartPackage.FromChartEntries([pendingEntry]);
            pendingPackage.path = pendingDirectoryPath;
            var library = new TestBmsLibrary(databaseScope, songDbPath)
            {
                BmsCharts = null,
                BmsonCharts = [installedBmson],
                ChartPackagesPending = CreatePackageCollection([pendingPackage]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };
            bool packageEntryNotificationObserved = false;
            Exception? packageEntryInspectionFailure = null;
            pendingEntry.PropertyChanged += (_, _) =>
            {
                packageEntryNotificationObserved = true;
                try
                {
                    Assert.IsFalse(library.IsWriteLockHeldPendingInstallCharts);
                    Assert.IsFalse(library.IsWriteLockHeldInitializeBMSFiles);
                    AssertLibraryGateIsNotHeldByCurrentThread(
                        library,
                        "rwlockBMSFilesInitializedAll");
                    AssertLibraryGateIsNotHeldByCurrentThread(
                        library,
                        "rwlockPendingInstallCharts");
                    AssertLibraryGateIsNotHeldByCurrentThread(
                        library,
                        "rwlockBMSFiles");
                }
                catch (Exception exception)
                {
                    packageEntryInspectionFailure = exception;
                }
            };

            library.InstallPendingPackagesToEstimatedDestinations([pendingPackage]);

            Assert.IsTrue(packageEntryNotificationObserved);
            Assert.IsNull(
                packageEntryInspectionFailure,
                packageEntryInspectionFailure?.ToString());
            Assert.AreEqual(0, library.ChartPackagesPending.Count);
            Assert.AreEqual(1, library.ChartPackagesInstalled.Count);
            ChartPackage displayPackage = library.ChartPackagesInstalled.Single();
            Assert.AreEqual(destinationDirectoryPath, displayPackage.path);
            ChartFile displayChart = displayPackage.ChartEntries.Single().Chart;
            Assert.AreSame(library.BmsonCharts.Single().Token, displayChart.Token);
            Assert.IsTrue(File.Exists(Path.Combine(destinationDirectoryPath, "sound.wav")));
        });
    }
    /// <summary>
    /// 資源のみの実導入を確定して外側排他を解放してから適用先を通知し、通知先が例外を投げても確定成功を維持することで、通知失敗の誤分類を検出する。
    /// </summary>
    /// <remarks>実ライブラリの保存・排他・通知経路に接続するため独立したSQLite DBと実フォルダを用いる。</remarks>
    // falseは通常購読、trueは通知先の例外を注入して成功保持を確認する。
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OverwritePendingInstalledOnlyPackagesResources_ReleasesOuterWritersBeforeEstimatedInstallPublication(
        bool subscriberThrows)
    {
        var databaseScope = new ApplicationDatabaseScope();

        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string destinationDirectoryPath = Path.Combine(tempRootPath, "installed-overwrite");
            string pendingDirectoryPath = Path.Combine(tempRootPath, "pending-overwrite");
            Directory.CreateDirectory(destinationDirectoryPath);
            Directory.CreateDirectory(pendingDirectoryPath);
            string installedBmsonPath = Path.Combine(destinationDirectoryPath, "chart.bmson");
            string pendingBmsonPath = Path.Combine(pendingDirectoryPath, "chart.bmson");
            string pendingResourcePath = Path.Combine(pendingDirectoryPath, "sound.wav");
            File.WriteAllText(installedBmsonPath, "{}");
            File.WriteAllText(pendingBmsonPath, "{}");
            File.WriteAllText(pendingResourcePath, "resource");
            ChartFile installedBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = installedBmsonPath,
                Folder = destinationDirectoryPath,
                RawTitle = "Installed",
                Md5 = "cccccccccccccccccccccccccccccccc",
                Sha256 = new string('d', 64)
            };
            ChartFile pendingBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = pendingBmsonPath,
                Folder = pendingDirectoryPath,
                RawTitle = "Pending",
                Md5 = installedBmson.Md5,
                Sha256 = installedBmson.Sha256
            };
            using (LR2SongDBExtended seedSongDb = databaseScope.OpenSongWrite(songDbPath))
            {
                seedSongDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(installedBmson), typeof(LR2SongDBExtended.bmson_song));
            }
            var pendingEntry = PackageChartEntry.FromChart(
                (pendingBmson));
            var pendingPackage = ChartPackage.FromChartEntries([pendingEntry]);
            pendingPackage.path = pendingDirectoryPath;
            var library = new TestBmsLibrary(databaseScope, songDbPath)
            {
                BmsCharts = null,
                BmsonCharts = [installedBmson],
                ChartPackagesPending = CreatePackageCollection([pendingPackage]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };
            installedBmson = library.BmsonCharts.Single();
            bool packageEntryNotificationObserved = false;
            Exception? packageEntryInspectionFailure = null;
            pendingEntry.PropertyChanged += (_, _) =>
            {
                try
                {
                    Assert.IsFalse(library.IsWriteLockHeldPendingInstallCharts);
                    Assert.IsFalse(library.IsWriteLockHeldInitializeBMSFiles);
                    using LibraryFileMutationLease lease = library.Lr2Synchronization.TryBeginMutation(
                        "test_notification_after_resource_overwrite");
                    Assert.IsNotNull(lease, "DST 通知中に outer file mutation lease が保持されています。");
                    Assert.AreEqual(0, library.ChartPackagesPending.Count);
                    Assert.AreEqual(1, library.ChartPackagesInstalled.Count);
                    Assert.AreSame(installedBmson.Token, library.BmsonCharts.Single().Token);
                    using LR2SongDBExtended notifiedSongDb = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
                    Assert.AreEqual(
                        1,
                        notifiedSongDb.ExecuteScalar<int>(
                            "SELECT COUNT(1) FROM bmson_song WHERE path = ?;",
                            installedBmsonPath));
                }
                catch (Exception exception)
                {
                    packageEntryInspectionFailure = exception;
                }
                packageEntryNotificationObserved = true;
                if (subscriberThrows)
                {
                    throw new InvalidOperationException("resource-overwrite-subscriber-marker");
                }
            };

            PendingInstalledOnlyResourceOverwriteResult result =
                library.OverwritePendingInstalledOnlyPackagesResources([pendingPackage]);

            Assert.AreEqual(1, result.SucceededInstall);
            Assert.AreEqual(0, result.Failed);
            Assert.IsTrue(result.HasDurableCommit);
            Assert.IsFalse(result.HasDurableFinalizationFailure);
            Assert.IsNotNull(result.SessionReceipt);
            Assert.IsFalse(result.SessionReceipt.HasRequiredFailure);
            Assert.AreSame(pendingEntry, pendingPackage.ChartEntries.Single());
            Assert.IsTrue(packageEntryNotificationObserved);
            Assert.IsNull(
                packageEntryInspectionFailure,
                packageEntryInspectionFailure?.ToString());
            Assert.AreEqual(0, library.ChartPackagesPending.Count);
            Assert.AreEqual(1, library.ChartPackagesInstalled.Count);
            Assert.IsTrue(File.Exists(Path.Combine(destinationDirectoryPath, "sound.wav")));
            Assert.AreSame(installedBmson.Token, library.BmsonCharts.Single().Token);
            Assert.AreEqual(installedBmsonPath, library.BmsonCharts.Single().Path);
            using LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(
                1,
                verifySongDb.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM bmson_song WHERE path = ?;",
                    installedBmsonPath));
            Assert.AreEqual(
                0,
                verifySongDb.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM bmson_song WHERE path = ?;",
                    pendingBmsonPath));
        });
    }
    /// <summary>
    /// 二回の資源のみ導入で所持譜面と取得済み索引を保ち、通知時のモデルロック解放・DB更新と終端後の警告・健全性・実資源を確認して、全件再構築や譜面追加への誤分類を検出する。背景16件は全件処理を見分ける入力である。
    /// </summary>
    /// <remarks>実ライブラリの保存・排他・通知経路に接続するため独立したSQLite DBと実フォルダを用いる。</remarks>
    // 背景16件は少数変更と全件処理を区別する入力で、処理時間の比較値ではない。
    [TestMethod]
    [DataRow(16)]
    public void OverwritePendingInstalledOnlyPackagesResources_UsesWarmCatalogWithoutChartDelta(int backgroundCount)
    {
        var databaseScope = new ApplicationDatabaseScope();

        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string backgroundDirectoryPath = Path.Combine(tempRootPath, "resource-only-warm-background");
            Directory.CreateDirectory(backgroundDirectoryPath);

            (string DestinationDirectoryPath, string PendingDirectoryPath,
                string FirstInstalledPath, string SecondInstalledPath,
                string FirstPendingPath, string SecondPendingPath,
                string FirstResourceName, string SecondResourceName,
                ChartFile FirstInstalled,
                ChartFile SecondInstalled,
                ChartPackage Package) CreateResourceStep(int step)
            {
                string destinationDirectoryPath = Path.Combine(
                    tempRootPath,
                    "resource-only-warm-installed-" + step);
                string pendingDirectoryPath = Path.Combine(
                    tempRootPath,
                    "resource-only-warm-pending-" + step);
                Directory.CreateDirectory(destinationDirectoryPath);
                Directory.CreateDirectory(pendingDirectoryPath);
                string firstResourceName = "sound-" + step + "-first.wav";
                string secondResourceName = "sound-" + step + "-second.wav";
                string firstMissingName = "missing-" + step + "-first.wav";
                string secondMissingName = "missing-" + step + "-second.wav";
                string firstInstalledPath = Path.Combine(destinationDirectoryPath, "first.bmson");
                string secondInstalledPath = Path.Combine(destinationDirectoryPath, "second.bmson");
                string firstPendingPath = Path.Combine(pendingDirectoryPath, "first.bmson");
                string secondPendingPath = Path.Combine(pendingDirectoryPath, "second.bmson");
                string firstJson = CreateBmsonJsonWithSounds(firstResourceName, firstMissingName);
                string secondJson = CreateBmsonJsonWithSounds(secondResourceName, secondMissingName);
                File.WriteAllText(firstInstalledPath, firstJson);
                File.WriteAllText(secondInstalledPath, secondJson);
                File.WriteAllText(firstPendingPath, firstJson);
                File.WriteAllText(secondPendingPath, secondJson);
                File.WriteAllBytes(Path.Combine(pendingDirectoryPath, firstResourceName), [1, 2, 3]);
                File.WriteAllBytes(Path.Combine(pendingDirectoryPath, secondResourceName), [4, 5, 6]);
                File.WriteAllBytes(Path.Combine(destinationDirectoryPath, firstMissingName), [7, 8, 9]);
                Assert.IsTrue(File.Exists(Path.Combine(destinationDirectoryPath, firstMissingName)));
                Assert.IsFalse(File.Exists(Path.Combine(pendingDirectoryPath, firstMissingName)));

                ChartFile installedFirst = ChartTestValues.ReadBmson(firstInstalledPath);
                ChartFile installedSecond = ChartTestValues.ReadBmson(secondInstalledPath);
                ChartFile pendingFirst = ChartTestValues.ReadBmson(firstPendingPath);
                ChartFile pendingSecond = ChartTestValues.ReadBmson(secondPendingPath);
                Assert.AreEqual(installedFirst.Md5, pendingFirst.Md5);
                Assert.AreEqual(installedFirst.Sha256, pendingFirst.Sha256);
                Assert.AreEqual(installedSecond.Md5, pendingSecond.Md5);
                Assert.AreEqual(installedSecond.Sha256, pendingSecond.Sha256);
                var firstEntry = PackageChartEntry.FromChart(
                    (pendingFirst));
                var secondEntry = PackageChartEntry.FromChart(
                    (pendingSecond));
                BmsLibraryPackageInstallService.ApplyPendingResourceHealthProjectionToEntries([firstEntry, secondEntry], initializeOriginal: true);
                Assert.AreEqual(50, firstEntry.Chart.WAVHealth);
                Assert.AreEqual(50, secondEntry.Chart.WAVHealth);
                Assert.IsTrue(firstEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
                Assert.IsTrue(secondEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
                var package = ChartPackage.FromChartEntries([firstEntry, secondEntry]);
                package.path = pendingDirectoryPath;
                package.delete_parent = false;
                return (
                    destinationDirectoryPath,
                    pendingDirectoryPath,
                    firstInstalledPath,
                    secondInstalledPath,
                    firstPendingPath,
                    secondPendingPath,
                    firstResourceName,
                    secondResourceName,
                    installedFirst,
                    installedSecond,
                    package);
            }

            (string DestinationDirectoryPath, string PendingDirectoryPath, string FirstInstalledPath, string SecondInstalledPath, string FirstPendingPath, string SecondPendingPath, string FirstResourceName, string SecondResourceName, ChartFile FirstInstalled, ChartFile SecondInstalled, ChartPackage Package) firstStep = CreateResourceStep(1);
            (string DestinationDirectoryPath, string PendingDirectoryPath, string FirstInstalledPath, string SecondInstalledPath, string FirstPendingPath, string SecondPendingPath, string FirstResourceName, string SecondResourceName, ChartFile FirstInstalled, ChartFile SecondInstalled, ChartPackage Package) secondStep = CreateResourceStep(2);
            var backgroundSongs = new List<ChartFile>(backgroundCount);
            for (int index = 0; index < backgroundCount; index++)
            {
                string path = Path.Combine(backgroundDirectoryPath, "background-" + index + ".bmson");
                File.WriteAllText(path, "{}");
                string hash = (index + 1).ToString("x8") + new string('e', 24);
                backgroundSongs.Add(ChartTestValues.Empty(ChartFileKind.Bmson) with
                {
                    Path = path,
                    Folder = backgroundDirectoryPath,
                    RawTitle = "Background " + index,
                    Md5 = hash,
                    Sha256 = (index + 1).ToString("x8") + new string('f', 56)
                });
            }
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(databaseScope, songDbPath, seedSongDb =>
            {
                seedSongDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(firstStep.FirstInstalled), typeof(LR2SongDBExtended.bmson_song));
                seedSongDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(firstStep.SecondInstalled), typeof(LR2SongDBExtended.bmson_song));
                seedSongDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(secondStep.FirstInstalled), typeof(LR2SongDBExtended.bmson_song));
                seedSongDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(secondStep.SecondInstalled), typeof(LR2SongDBExtended.bmson_song));
                foreach (ChartFile backgroundSong in backgroundSongs)
                {
                    seedSongDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(backgroundSong), typeof(LR2SongDBExtended.bmson_song));
                }
            });

            var library = new TestBmsLibrary(databaseScope, songDbPath)
            {
                BmsCharts = null,
                BmsonCharts = [
                    firstStep.FirstInstalled,
                    firstStep.SecondInstalled,
                    secondStep.FirstInstalled,
                    secondStep.SecondInstalled,
                    .. backgroundSongs
                ],
                ChartPackagesPending = CreatePackageCollection([firstStep.Package]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };
            library.FixtureSettings.StandaloneBmsRootPaths = StandaloneBmsRootPathSettings.Serialize([firstStep.DestinationDirectoryPath, secondStep.DestinationDirectoryPath]);

            // cold構築では実root列挙を一度だけ観測し、warm区間のsource workと
            // 混同しないよう観測口を切り替えます。
            List<string> coldHashWork = [];
            List<string> coldInstalledWork = [];
            library.OwnedChartHashIndexStoreWorkObserver = coldHashWork.Add;
            library.InstalledChartLookupStoreWorkObserver = coldInstalledWork.Add;
            OwnedChartCollectionTestSupport.SetLibraryBmsonSongsWithoutNotification(library, []);
            OwnedChartCollectionTestSupport.SetLibraryBmsonSongsWithoutNotification(
                library,
                [
                    firstStep.FirstInstalled,
                    firstStep.SecondInstalled,
                    secondStep.FirstInstalled,
                    secondStep.SecondInstalled,
                    .. backgroundSongs
                ]);
            _ = library.GetOwnedChartHashIndexSnapshot();
            InstalledChartLookupIndexSnapshot coldInstalled =
                OwnedChartCollectionTestSupport.InvokeCreateInstalledChartLookupSnapshot(library);
            _ = coldInstalled.Md5Directories.ToArray();
            _ = coldInstalled.Sha256Directories.ToArray();
            _ = coldInstalled.PrimaryHashCounts.ToArray();
            Assert.IsTrue(coldHashWork.Contains("owned_hash_source_enumeration"));
            Assert.IsTrue(coldHashWork.Contains("owned_hash_source_entry_visited"));
            Assert.IsTrue(coldInstalledWork.Contains("installed_root_map_enumeration"));
            Assert.IsTrue(coldInstalledWork.Contains("installed_root_map_key_visited"));
            Assert.IsTrue(coldInstalledWork.Contains("installed_primary_hash_count_update"));
            Assert.IsTrue(coldInstalledWork.Contains("installed_directory_bucket_update"));
            library.OwnedChartHashIndexStoreWorkObserver = null;
            library.InstalledChartLookupStoreWorkObserver = null;

            OwnedChartHashIndexVersionedSnapshot oldHash = library.GetOwnedChartHashIndexSnapshot();
            InstalledChartLookupIndexSnapshot oldInstalled =
                OwnedChartCollectionTestSupport.InvokeCreateInstalledChartLookupSnapshot(library);
            PlaylistLibraryResolveIndexSnapshot oldPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool oldPlaylistCacheHit,
                out int oldPlaylistStaleRetries);
            Assert.IsFalse(oldPlaylistCacheHit);
            Assert.AreEqual(0, oldPlaylistStaleRetries);
            Assert.AreEqual(backgroundCount + 4, library.BmsonCharts.Count);

            List<string> hashWork = [];
            List<string> playlistWork = [];
            List<string> installedWork = [];
            library.OwnedChartHashIndexStoreWorkObserver = hashWork.Add;
            library.PlaylistLibraryResolveIndexStoreWorkObserver = playlistWork.Add;
            library.InstalledChartLookupStoreWorkObserver = installedWork.Add;
            OwnedChartHashIndexVersionedSnapshot? snapshotAfterFirstHash = null;
            InstalledChartLookupIndexSnapshot? snapshotAfterFirstInstalled = null;
            PlaylistLibraryResolveIndexSnapshot? snapshotAfterFirstPlaylist = null;
            for (int stepIndex = 0; stepIndex < 2; stepIndex++)
            {
                (string DestinationDirectoryPath, string PendingDirectoryPath, string FirstInstalledPath, string SecondInstalledPath, string FirstPendingPath, string SecondPendingPath, string FirstResourceName, string SecondResourceName, ChartFile FirstInstalled, ChartFile SecondInstalled, ChartPackage Package) step = stepIndex == 0 ? firstStep : secondStep;
                if (stepIndex > 0)
                {
                    library.ChartPackagesPending = CreatePackageCollection([step.Package]);
                }

                bool packageEntryNotificationObserved = false;
                Exception? packageEntryInspectionFailure = null;
                void InspectPackageEntryNotification(object? _, System.ComponentModel.PropertyChangedEventArgs? __)
                {
                    try
                    {
                        Assert.IsFalse(library.IsWriteLockHeldPendingInstallCharts);
                        using LR2SongDBExtended notifiedSongDb = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
                        Assert.AreEqual(
                            1,
                            notifiedSongDb.ExecuteScalar<int>(
                                "SELECT COUNT(1) FROM bmson_song WHERE path = ?;",
                                step.FirstInstalledPath));
                    }
                    catch (Exception exception)
                    {
                        packageEntryInspectionFailure = exception;
                    }
                    packageEntryNotificationObserved = true;
                }
                PackageChartEntry observedPendingEntry = step.Package.ChartEntries[0];
                observedPendingEntry.PropertyChanged += InspectPackageEntryNotification;
                int previousHashWorkCount = hashWork.Count;
                int previousInstalledWorkCount = installedWork.Count;
                int previousPlaylistWorkCount = playlistWork.Count;
                try
                {
                    PendingInstalledOnlyResourceOverwriteResult result =
                        library.OverwritePendingInstalledOnlyPackagesResources([step.Package]);

                    Assert.AreEqual(1, result.SucceededInstall);
                    Assert.AreEqual(0, result.Failed);
                }
                finally
                {
                    observedPendingEntry.PropertyChanged -= InspectPackageEntryNotification;
                }

                Assert.AreEqual(2, step.Package.ChartEntries.Count);
                Assert.AreSame(observedPendingEntry, step.Package.ChartEntries[0]);
                Assert.IsTrue(packageEntryNotificationObserved);
                Assert.IsNull(packageEntryInspectionFailure, packageEntryInspectionFailure?.ToString());
                Assert.AreEqual(0, library.ChartPackagesPending.Count);
                Assert.AreEqual(stepIndex + 1, library.ChartPackagesInstalled.Count);
                CollectionAssert.AreEqual(
                    new byte[] { 1, 2, 3 },
                    File.ReadAllBytes(Path.Combine(step.DestinationDirectoryPath, step.FirstResourceName)));
                CollectionAssert.AreEqual(
                    new byte[] { 4, 5, 6 },
                    File.ReadAllBytes(Path.Combine(step.DestinationDirectoryPath, step.SecondResourceName)));
                Assert.AreEqual(
                    step.FirstInstalled.Md5,
                    library.BmsonCharts.Single(song => song.Path == step.FirstInstalledPath).Md5);
                Assert.AreEqual(
                    step.SecondInstalled.Md5,
                    library.BmsonCharts.Single(song => song.Path == step.SecondInstalledPath).Md5);

                PackageChartEntry installedFirstEntry = library.ChartPackagesInstalled
                    .SelectMany(package => package.ChartEntries)
                    .Single(entry => entry.Chart.Md5 == step.FirstInstalled.Md5);
                PackageChartEntry installedSecondEntry = library.ChartPackagesInstalled
                    .SelectMany(package => package.ChartEntries)
                    .Single(entry => entry.Chart.Md5 == step.SecondInstalled.Md5);
                Assert.AreEqual(step.FirstInstalledPath, installedFirstEntry.Chart.Path);
                Assert.AreEqual(step.SecondInstalledPath, installedSecondEntry.Chart.Path);
                Assert.AreSame(library.BmsonCharts.Single(chart => chart.Path == step.FirstInstalledPath).Token, installedFirstEntry.Chart.Token);
                Assert.AreSame(library.BmsonCharts.Single(chart => chart.Path == step.SecondInstalledPath).Token, installedSecondEntry.Chart.Token);
                Assert.AreEqual(100, installedFirstEntry.Chart.WAVHealth);
                Assert.AreEqual(50, installedSecondEntry.Chart.WAVHealth);
                Assert.IsFalse(installedFirstEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
                Assert.IsTrue(installedSecondEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));

                using (LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
                {
                    Assert.AreEqual(backgroundCount + 4, verifySongDb.Table<LR2SongDBExtended.bmson_song>().Count());
                    Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM bmson_song WHERE path = ?;", step.FirstInstalledPath));
                    Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM bmson_song WHERE path = ?;", step.SecondInstalledPath));
                    Assert.AreEqual(0, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM bmson_song WHERE path = ?;", step.FirstPendingPath));
                    Assert.AreEqual(0, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM bmson_song WHERE path = ?;", step.SecondPendingPath));
                    LR2SongDBExtended.bmson_song persisted = verifySongDb.Table<LR2SongDBExtended.bmson_song>()
                        .Single(song => song.path == step.FirstInstalledPath);
                    Assert.AreEqual(step.FirstInstalled.Md5, persisted.md5);
                    Assert.AreEqual(step.FirstInstalled.Sha256, persisted.sha256);
                }

                OwnedChartHashIndexVersionedSnapshot updatedHash = library.GetOwnedChartHashIndexSnapshot();
                OwnedChartHashIndexVersionedSnapshot cachedHash = library.GetOwnedChartHashIndexSnapshot();
                InstalledChartLookupIndexSnapshot updatedInstalled =
                    OwnedChartCollectionTestSupport.InvokeCreateInstalledChartLookupSnapshot(library);
                InstalledChartLookupIndexSnapshot cachedInstalled =
                    OwnedChartCollectionTestSupport.InvokeCreateInstalledChartLookupSnapshot(library);
                PlaylistLibraryResolveIndexSnapshot updatedPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                    CancellationToken.None,
                    out bool updatedPlaylistCacheHit,
                    out int updatedPlaylistStaleRetries);
                PlaylistLibraryResolveIndexSnapshot cachedPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                    CancellationToken.None,
                    out bool cachedPlaylistCacheHit,
                    out int cachedPlaylistStaleRetries);
                Assert.AreSame(oldHash, updatedHash);
                Assert.AreSame(updatedHash, cachedHash);
                Assert.AreSame(oldInstalled, updatedInstalled);
                Assert.AreSame(updatedInstalled, cachedInstalled);
                Assert.AreSame(oldPlaylist, updatedPlaylist);
                Assert.AreSame(updatedPlaylist, cachedPlaylist);
                Assert.IsTrue(updatedPlaylistCacheHit, string.Join(";", playlistWork));
                Assert.AreEqual(0, updatedPlaylistStaleRetries);
                Assert.IsTrue(cachedPlaylistCacheHit);
                Assert.AreEqual(0, cachedPlaylistStaleRetries);
                Assert.IsTrue(oldHash.ContainsMd5(backgroundSongs[0].Md5));
                Assert.IsTrue(oldInstalled.ContainsPrimaryHash(backgroundSongs[0].Md5));
                Assert.IsTrue(oldPlaylist.ContainsCandidate(ChartFileKind.Bmson, backgroundSongs[0].Path));
                if (stepIndex == 0)
                {
                    snapshotAfterFirstHash = updatedHash;
                    snapshotAfterFirstInstalled = updatedInstalled;
                    snapshotAfterFirstPlaylist = updatedPlaylist;
                }
                else
                {
                    Assert.AreSame(snapshotAfterFirstHash, updatedHash);
                    Assert.AreSame(snapshotAfterFirstInstalled, updatedInstalled);
                    Assert.AreSame(snapshotAfterFirstPlaylist, updatedPlaylist);
                }
                string[] operationHashWork = [.. hashWork.Skip(previousHashWorkCount)];
                string[] operationInstalledWork = [.. installedWork.Skip(previousInstalledWorkCount)];
                string[] operationPlaylistWork = [.. playlistWork.Skip(previousPlaylistWorkCount)];
                Assert.IsFalse(operationHashWork.Contains("owned_hash_source_enumeration"));
                Assert.IsFalse(operationHashWork.Contains("owned_hash_source_entry_visited"));
                Assert.IsFalse(operationInstalledWork.Contains("installed_root_map_enumeration"));
                Assert.IsFalse(operationInstalledWork.Contains("installed_root_map_key_visited"));
                Assert.IsTrue(
                    operationInstalledWork.Count(operation => operation == "installed_directory_bucket_entry_copied") < backgroundCount,
                    "resource-only operation copied an entire directory bucket.");
                Assert.IsFalse(operationPlaylistWork.Contains("playlist_resolve_source_enumeration"));
                Assert.IsFalse(operationPlaylistWork.Contains("playlist_resolve_full_root_enumeration"));
                Assert.IsFalse(operationPlaylistWork.Contains("playlist_resolve_full_root_key_visited"));
            }
        });
    }

    /// <summary>
    /// 元入力の片付けだけでも外側排他を保って再入要求を拒否し、片付けを実行しない成功や途中の追加導入を検出する。
    /// </summary>
    /// <remarks>実ライブラリの保存・排他・通知経路に接続するため独立したSQLite DBと実フォルダを用いる。</remarks>
    [TestMethod]
    public void OverwritePendingInstalledOnlyPackagesResources_CleanupOnlyUsesOuterLeaseAndExcludesReentry()
    {
        var databaseScope = new ApplicationDatabaseScope();

        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string destinationDirectoryPath = Path.Combine(tempRootPath, "installed-cleanup-only");
            string pendingDirectoryPath = Path.Combine(tempRootPath, "pending-cleanup-only");
            Directory.CreateDirectory(destinationDirectoryPath);
            Directory.CreateDirectory(pendingDirectoryPath);
            string installedBmsonPath = Path.Combine(destinationDirectoryPath, "chart.bmson");
            string pendingBmsonPath = Path.Combine(pendingDirectoryPath, "chart.bmson");
            File.WriteAllText(installedBmsonPath, "{}");
            File.WriteAllText(pendingBmsonPath, "{}");
            ChartFile installedBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = installedBmsonPath,
                Folder = destinationDirectoryPath,
                RawTitle = "Installed cleanup-only",
                Md5 = "11111111111111111111111111111111",
                Sha256 = new string('2', 64)
            };
            ChartFile pendingBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = pendingBmsonPath,
                Folder = pendingDirectoryPath,
                RawTitle = "Pending cleanup-only",
                Md5 = installedBmson.Md5,
                Sha256 = installedBmson.Sha256
            };
            var pendingEntry = PackageChartEntry.FromChart(
                (pendingBmson));
            var pendingPackage = ChartPackage.FromChartEntries([pendingEntry]);
            pendingPackage.path = pendingDirectoryPath;
            pendingPackage.delete_parent = false;
            ChartPackage reentryPackage = new()
            {
                path = Path.Combine(tempRootPath, "reentry-cleanup-only")
            };
            var fileMutationService = new ReentrantCleanupFileMutationService();
            var library = new TestBmsLibrary(databaseScope,
                songDbPath,
                null,
                null,
                fileMutationService,
                new RecordingDialogService(),
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                    DeletePendingPackageSourceAfterInstall = true,
                    FolderNameFormat = "%TITLE%",
                    BMSInstallDir = Path.Combine(tempRootPath, "auto-install")
                });
            library.BmsCharts = null;
            library.BmsonCharts = [installedBmson];
            library.ChartPackagesPending = CreatePackageCollection([pendingPackage]);
            fileMutationService.Configure(library, reentryPackage);

            PendingInstalledOnlyResourceOverwriteResult result;
            try
            {
                result = library.OverwritePendingInstalledOnlyPackagesResources([pendingPackage]);
            }
            finally
            {
                fileMutationService.WaitForReentryCompletion();
            }

            Assert.IsNull(fileMutationService.ReentryFailure, fileMutationService.ReentryFailure?.ToString());
            Assert.IsTrue(
                fileMutationService.ReentryCompletedDuringCleanup,
                "Cleanup-only processing did not expose the outer lease's reentry boundary.");
            Assert.IsNotNull(fileMutationService.ReentryResult);
            Assert.AreEqual(0, fileMutationService.ReentryResult.Requested);
            Assert.AreEqual(1, result.SucceededCleanupOnly);
            Assert.AreEqual(0, result.SucceededInstall);
            Assert.AreEqual(0, result.Failed);
            Assert.AreEqual(0, library.ChartPackagesPending.Count);
            Assert.IsFalse(Directory.Exists(pendingDirectoryPath));
        });
    }


    /// <summary>
    /// 保留一覧にないパッケージの導入要求は入力ファイルとパスを変更せず、対象外入力への物理作用を検出する。
    /// </summary>
    /// <remarks>実ライブラリの保存・排他・通知経路に接続するため独立したSQLite DBと実フォルダを用いる。</remarks>
    [TestMethod]
    public void InstallPendingPackagesToEstimatedDestinations_LeavesPackageUnchangedWhenItIsNotPending()
    {
        var databaseScope = new ApplicationDatabaseScope();

        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string pendingDirectoryPath = Path.Combine(tempRootPath, "pending-not-selected");
            Directory.CreateDirectory(pendingDirectoryPath);
            string chartPath = Path.Combine(pendingDirectoryPath, "chart.bms");
            File.WriteAllText(chartPath, "#TITLE pending");
            ChartPackage requestedPackage = ChartPackageTestExtensions.CreatePackage(
                [CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath)]);
            requestedPackage.path = pendingDirectoryPath;
            requestedPackage.delete_parent = false;

            var library = new TestBmsLibrary(databaseScope, songDbPath)
            {
                ChartPackagesPending = CreatePackageCollection([])
            };

            library.InstallPendingPackagesToEstimatedDestinations([requestedPackage]);

            Assert.AreEqual(0, library.ChartPackagesPending.Count);
            Assert.IsTrue(File.Exists(chartPath));
            Assert.AreEqual(pendingDirectoryPath, requestedPackage.path);
            Assert.AreEqual(string.Empty, requestedPackage.ChartEntries.Single().Chart.InstallDestination);
        });
    }

    /// <summary>
    /// LR2同期中でもnullの導入要求は引数失敗を伝え、稼働状態を理由に不正要求を成功扱いする誤りを検出する。
    /// </summary>
    /// <remarks>実ライブラリの保存・排他・通知経路に接続するため独立したSQLite DBと実フォルダを用いる。</remarks>
    [TestMethod]
    public void InstallPendingPackagesToEstimatedDestinations_NullStillThrowsWhenLr2SyncIsRunning()
    {
        var databaseScope = new ApplicationDatabaseScope();

        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            var library = new TestBmsLibrary(databaseScope, songDbPath);
            library.Lr2Synchronization.Running = true;
            try
            {
                Assert.ThrowsExactly<ArgumentNullException>(
                    () => library.InstallPendingPackagesToEstimatedDestinations(null));
            }
            finally
            {
                library.Lr2Synchronization.Running = false;
            }
        });
    }
    /// <summary>
    /// 別変更が受付を保持する間の自動導入はBusy例外で失敗し、処理しない空結果を成功として返す誤りを検出する。
    /// </summary>
    /// <remarks>実ライブラリの保存・排他・通知経路に接続するため独立したSQLite DBと実フォルダを用いる。</remarks>
    [TestMethod]
    public void InstallChartPackagesAutoWithProgress_WhenAnotherFileMutationOwnsAdmission_FailsInsteadOfPublishingEmptySuccess()
    {
        var databaseScope = new ApplicationDatabaseScope();

        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string sourceDirectory = Path.Combine(tempRootPath, "busy-auto-install-source");
            Directory.CreateDirectory(sourceDirectory);
            var library = new TestBmsLibrary(databaseScope, songDbPath);
            using LibraryFileMutationLease incumbent = library.Lr2Synchronization.TryBeginMutation(
                "test_busy_auto_install");
            Assert.IsNotNull(incumbent);

            Assert.ThrowsExactly<InvalidOperationException>(
                () => library.InstallChartPackagesAutoWithProgressAsync([sourceDirectory], CancellationToken.None, new RecordingPackageInstallProgressWriter()).GetAwaiter().GetResult());
        });
    }

    /// <summary>
    /// ZIP・7z・RAR・LZHの実入力を展開して保存日時と進捗を復元し、形式別の展開・必須日時復元の欠落を検出する。
    /// </summary>
    /// <remarks>実native展開と日時を確認し、作成時に捕捉した当該ケースの展開先だけを回収する。native系列の非並列条件を維持する。</remarks>
    [TestMethod]
    [DataRow("fixture.zip")]
    [DataRow("fixture.7z")]
    [DataRow("fixture.rar")]
    [DataRow("fixture.lzh")]
    [DoNotParallelize]
    public void ExpandInstallSources_ExtractsSupportedArchiveAndRestoresLastWriteTime(string archiveFileName)
    {

        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string archivePath = Path.Combine(tempDirectoryPath, archiveFileName);
            File.Copy(GetArchiveFixturePath(archiveFileName), archivePath);
            var service = new BmsLibraryPackageInstallService();
            List<string> logs = [];
            var progressWriter = new RecordingPackageInstallProgressWriter();
            List<string> createdInputs = [];

            try
            {
                List<string> expandedPaths = service.ExpandInstallSourcesWithProgress(
                    [archivePath],
                    new RealFileMutationService(),
                    new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly),
                    logs.Add,
                    new UnexpectedBmsLibraryDialogService(),
                    progressWriter,
                    CancellationToken.None,
                    action => action(),
                    createdInputs.Add);

                Assert.AreEqual(1, expandedPaths.Count);
                string extractedDirectoryPath = expandedPaths[0];
                string extractedChartPath = Path.Combine(extractedDirectoryPath, "maybe_H.bms");
                Assert.IsTrue(File.Exists(extractedChartPath));
                Assert.AreEqual(GetExpectedArchiveLastWriteTime(), File.GetLastWriteTime(extractedChartPath));
                Assert.AreEqual(1, progressWriter.SourceProcessedCount);
                CollectionAssert.AreEqual(
                    new[] { "1/1:" + archiveFileName },
                    progressWriter.ArchiveStarts.ToArray());
                Assert.IsFalse(logs.Any(message => message.IndexOf("extract_failed", StringComparison.OrdinalIgnoreCase) >= 0));
                Assert.IsFalse(logs.Any(message => message.IndexOf("metadata_restore_required_failed", StringComparison.OrdinalIgnoreCase) >= 0));
                using FileStream reopenedSource = File.Open(archivePath, FileMode.Open, FileAccess.Read, FileShare.None);
                Assert.AreEqual(new FileInfo(GetArchiveFixturePath(archiveFileName)).Length, reopenedSource.Length);
            }
            finally
            {
                CleanupCreatedExpansionPaths(createdInputs);
            }
        });
    }

    /// <summary>
    /// 各実書庫の必須更新日時復元が失敗したら展開先を受理せず進捗と失敗通知を残し、復元失敗を正常入力へ読み替える誤りを検出する。
    /// </summary>
    /// <remarks>実native展開と日時を確認し、作成時に捕捉した当該ケースの展開先だけを回収する。native系列の非並列条件を維持する。</remarks>
    [TestMethod]
    [DataRow("fixture.zip")]
    [DataRow("fixture.7z")]
    [DataRow("fixture.rar")]
    [DataRow("fixture.lzh")]
    [DoNotParallelize]
    public void ExpandInstallSources_AbortsArchiveWhenRequiredLastWriteRestoreFails(string archiveFileName)
    {

        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string archivePath = Path.Combine(tempDirectoryPath, archiveFileName);
            File.Copy(GetArchiveFixturePath(archiveFileName), archivePath);
            var service = new BmsLibraryPackageInstallService();
            List<string> logs = [];
            var dialogService = new RecordingDialogService();
            var progressWriter = new RecordingPackageInstallProgressWriter();
            List<string> createdInputs = [];

            try
            {
                List<string> expandedPaths = service.ExpandInstallSourcesWithProgress(
                    [archivePath],
                    new FailingLastWriteFileMutationService(),
                    new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly),
                    logs.Add,
                    dialogService,
                    progressWriter,
                    CancellationToken.None,
                    action => action(),
                    createdInputs.Add);

                Assert.AreEqual(0, expandedPaths.Count);
                Assert.AreEqual(1, progressWriter.SourceProcessedCount);
                CollectionAssert.AreEqual(
                    new[] { "1/1:" + archiveFileName },
                    progressWriter.ArchiveStarts.ToArray());
                Assert.IsTrue(logs.Any(message => message.IndexOf("metadata_restore_required_failed", StringComparison.OrdinalIgnoreCase) >= 0));
                Assert.IsTrue(logs.Any(message => message.IndexOf(".bms", StringComparison.OrdinalIgnoreCase) >= 0));
                Assert.AreEqual(1, dialogService.Messages.Count);
                Assert.IsTrue(dialogService.Messages[0].IndexOf("更新日時", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            finally
            {
                CleanupCreatedExpansionPaths(createdInputs);
            }
        });
    }

    /// <summary>
    /// 通常フォルダ・書庫名フォルダ・大文字拡張子の二実書庫を混在させ、両フォルダの保持と実展開、書庫だけの番号・総数を確認します。
    /// </summary>
    /// <remarks>捕捉した当該展開先だけを回収します。既存のnative系列は共通一時領域も回収するため、非並列を維持します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void ExpandInstallSources_ReportsArchiveOrdinalAmongArchivesOnly()
    {
        WithTemporaryDirectory(tempDirectoryPath =>
        {
            string directoryPath = Path.Combine(tempDirectoryPath, "source-folder");
            Directory.CreateDirectory(directoryPath);
            string archiveNamedDirectory = Path.Combine(tempDirectoryPath, "work.ZIP");
            Directory.CreateDirectory(archiveNamedDirectory);
            string firstArchivePath = Path.Combine(tempDirectoryPath, "first.ZIP");
            string secondArchivePath = Path.Combine(tempDirectoryPath, "second.7Z");
            byte[] firstArchiveBytes = File.ReadAllBytes(GetArchiveFixturePath("fixture.zip"));
            byte[] secondArchiveBytes = File.ReadAllBytes(GetArchiveFixturePath("fixture.7z"));
            File.WriteAllBytes(firstArchivePath, firstArchiveBytes);
            File.WriteAllBytes(secondArchivePath, secondArchiveBytes);
            var service = new BmsLibraryPackageInstallService();
            var progressWriter = new RecordingPackageInstallProgressWriter();
            var dialogs = new RecordingDialogService();
            List<string> createdInputs = [];
            try
            {
                List<string> expandedPaths = service.ExpandInstallSourcesWithProgress(
                    [directoryPath, firstArchivePath, archiveNamedDirectory, secondArchivePath],
                    new RealFileMutationService(),
                    new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly),
                    null,
                    dialogs,
                    progressWriter,
                    CancellationToken.None,
                    action => action(), createdInputs.Add);

                Assert.AreEqual(4, expandedPaths.Count);
                Assert.AreEqual(4, progressWriter.SourceProcessedCount);
                CollectionAssert.AreEquivalent(new[] { directoryPath, archiveNamedDirectory },
                    expandedPaths.Except(createdInputs).ToArray());
                Assert.AreEqual(2, createdInputs.Count);
                Assert.AreEqual(0, dialogs.Messages.Count);
                using (ZipArchive reference = ZipFile.OpenRead(GetArchiveFixturePath("fixture.zip")))
                using (Stream content = (reference.GetEntry("maybe_H.bms") ?? throw new AssertFailedException("比較元の譜面がありません。")).Open())
                using (var bytes = new MemoryStream())
                {
                    content.CopyTo(bytes);
                    foreach (string extractedRoot in createdInputs)
                    {
                        CollectionAssert.AreEqual(bytes.ToArray(), File.ReadAllBytes(Path.Combine(extractedRoot, "maybe_H.bms")));
                    }
                }
                CollectionAssert.AreEqual(firstArchiveBytes, File.ReadAllBytes(firstArchivePath));
                CollectionAssert.AreEqual(secondArchiveBytes, File.ReadAllBytes(secondArchivePath));
                CollectionAssert.AreEqual(
                    new[] { "1/2:first.ZIP", "2/2:second.7Z" },
                    progressWriter.ArchiveStarts.ToArray());
            }
            finally
            {
                foreach (string path in createdInputs)
                {
                    TempDirectoryPublisher.TryDeleteManagedPath(path,
                        logWarning: (failedPath, exception) => Trace.WriteLine("展開先の回収失敗: " + failedPath + " " + exception));
                }
            }
            Assert.IsTrue(createdInputs.All(path => !Directory.Exists(path)), "当該テストの展開先を回収します。");
        });
    }

    /// <summary>
    /// 暗号化・破損7zと短い・切断RARの展開失敗では借用元bytesと排他再読取りを保ってprivate準備先を削除し、失敗通知と進捗を残します。
    /// </summary>
    /// <remarks>実native展開と借用元を確認し、作成時に捕捉した当該ケースの展開先だけを回収する。native系列の非並列条件を維持する。</remarks>
    [TestMethod]
    [DataRow("encrypted.7z")]
    [DataRow("corrupt.7z")]
    [DataRow("short.rar")]
    [DataRow("truncated.rar")]
    [DoNotParallelize]
    public void ExpandInstallSources_RejectsArchiveFailureWithoutConsumingSource(string archiveFileName)
    {

        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string archivePath = Path.Combine(tempDirectoryPath, archiveFileName);
            if (string.Equals(archiveFileName, "corrupt.7z", StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(archivePath, "not a valid archive");
            }
            else if (archiveFileName == "short.rar")
            {
                File.WriteAllBytes(archivePath, [0x52, 0x61, 0x72]);
            }
            else if (archiveFileName == "truncated.rar")
            {
                File.WriteAllBytes(archivePath, File.ReadAllBytes(GetArchiveFixturePath("fixture.rar"))[..16]);
            }
            else
            {
                File.Copy(GetArchiveFixturePath(archiveFileName), archivePath);
            }

            byte[] originalSource = File.ReadAllBytes(archivePath);
            List<string> createdRoots = [];
            var service = new BmsLibraryPackageInstallService();
            List<string> logs = [];
            var dialogService = new RecordingDialogService();
            var progressWriter = new RecordingPackageInstallProgressWriter();

            try
            {
                List<string> expandedPaths = service.ExpandInstallSourcesWithProgress(
                    [archivePath],
                    new RealFileMutationService(),
                    new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly),
                    logs.Add,
                    dialogService,
                    progressWriter,
                    CancellationToken.None,
                    action => action(),
                    createdRoots.Add);

                Assert.AreEqual(0, expandedPaths.Count);
                Assert.AreEqual(1, progressWriter.SourceProcessedCount);
                Assert.IsTrue(File.Exists(archivePath));
                Assert.IsTrue(logs.Any(message => message.IndexOf("extract_failed", StringComparison.OrdinalIgnoreCase) >= 0));
                Assert.AreEqual(1, dialogService.Messages.Count);

                Assert.IsFalse(Directory.Exists(createdRoots.Single()));
                CollectionAssert.AreEqual(originalSource, File.ReadAllBytes(archivePath));
                using FileStream reopenedSource = File.Open(archivePath, FileMode.Open, FileAccess.Read, FileShare.None);
                Assert.AreEqual(originalSource.Length, reopenedSource.Length);
            }
            finally
            {
                CleanupCreatedExpansionPaths(createdRoots);
            }
        });
    }

    /// <summary>借用元を他者が排他保持して読取りIOが失敗しても、そのprivate展開先だけを回収し、独立した正常ZIPのbytes・必須日時と進捗を保持します。</summary>
    /// <remarks>テスト自身が排他Streamを所有し、解除後の借用元bytesと排他再読取りを確認します。当該展開先だけを回収し、native系列の非並列条件を維持します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void ExpandInstallSources_SourceReadFailurePreservesBorrowedSourceAndIndependentArchive()
    {
        WithTemporaryDirectory(tempDirectoryPath =>
        {
            byte[] original = File.ReadAllBytes(GetArchiveFixturePath("fixture.zip"));
            string blocked = Path.Combine(tempDirectoryPath, "blocked.zip");
            string normal = Path.Combine(tempDirectoryPath, "normal.zip");
            File.WriteAllBytes(blocked, original);
            File.WriteAllBytes(normal, original);
            List<string> roots = [];
            var progress = new RecordingPackageInstallProgressWriter();
            var dialogs = new RecordingDialogService();
            try
            {
                List<string> expanded;
                using (FileStream blockedSource = File.Open(blocked, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    expanded = new BmsLibraryPackageInstallService().ExpandInstallSourcesWithProgress(
                        [blocked, normal], new RealFileMutationService(),
                        new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly), null, dialogs, progress,
                        CancellationToken.None, action => action(), roots.Add);
                }
                Assert.AreEqual(1, expanded.Count);
                Assert.AreEqual(roots[1], expanded.Single());
                Assert.IsFalse(Directory.Exists(roots[0]));
                using (ZipArchive reference = ZipFile.OpenRead(GetArchiveFixturePath("fixture.zip")))
                {
                    foreach (ZipArchiveEntry entry in reference.Entries)
                    {
                        using Stream content = entry.Open();
                        using var bytes = new MemoryStream();
                        content.CopyTo(bytes);
                        CollectionAssert.AreEqual(bytes.ToArray(), File.ReadAllBytes(Path.Combine(expanded.Single(), entry.FullName)));
                    }
                }
                Assert.AreEqual(GetExpectedArchiveLastWriteTime(), File.GetLastWriteTime(Path.Combine(expanded.Single(), "maybe_H.bms")));
                CollectionAssert.AreEqual(original, File.ReadAllBytes(blocked));
                CollectionAssert.AreEqual(original, File.ReadAllBytes(normal));
                using FileStream blockedReopened = File.Open(blocked, FileMode.Open, FileAccess.Read, FileShare.None);
                using FileStream normalReopened = File.Open(normal, FileMode.Open, FileAccess.Read, FileShare.None);
                Assert.AreEqual(2, progress.SourceProcessedCount);
                Assert.AreEqual(1, dialogs.Messages.Count);
                StringAssert.Contains(dialogs.Messages.Single(), blocked);
            }
            finally { CleanupCreatedExpansionPaths(roots); }
        });
    }

    /// <summary>診断先が通常例外を返しても壊れた借用ZIPだけを除外し、独立した正常ZIPのbytes・進捗と借用元を保持します。</summary>
    /// <remarks>実native展開を非並列で実行し、診断callback内の表明に頼らず実結果を観測します。回収は当該展開先だけに限定します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void ExpandInstallSources_DiagnosticFailureDoesNotRejectIndependentNormalArchive()
    {
        WithTemporaryDirectory(tempDirectoryPath =>
        {
            string broken = Path.Combine(tempDirectoryPath, "broken.zip");
            File.WriteAllBytes(broken, [1, 2, 3]);
            string normal = Path.Combine(tempDirectoryPath, "normal.zip");
            byte[] original = File.ReadAllBytes(GetArchiveFixturePath("fixture.zip"));
            File.WriteAllBytes(normal, original);
            var progress = new RecordingPackageInstallProgressWriter();
            var dialogs = new RecordingDialogService();
            List<string> roots = [];
            int diagnosticAttempts = 0;
            try
            {
                List<string> expanded = new BmsLibraryPackageInstallService().ExpandInstallSourcesWithProgress(
                    [broken, normal], new RealFileMutationService(),
                    new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly),
                    _ => { diagnosticAttempts++; throw new InvalidOperationException("diagnostic failure"); },
                    dialogs, progress, CancellationToken.None, action => action(), roots.Add);
                Assert.AreEqual(1, expanded.Count);
                Assert.AreEqual(roots[1], expanded.Single());
                Assert.IsFalse(Directory.Exists(roots[0]));
                using (ZipArchive reference = ZipFile.OpenRead(GetArchiveFixturePath("fixture.zip")))
                using (Stream content = (reference.GetEntry("maybe_H.bms") ?? throw new AssertFailedException("比較元の譜面がありません。")).Open())
                using (var bytes = new MemoryStream())
                {
                    content.CopyTo(bytes);
                    CollectionAssert.AreEqual(bytes.ToArray(), File.ReadAllBytes(Path.Combine(expanded.Single(), "maybe_H.bms")));
                }
                CollectionAssert.AreEqual(original, File.ReadAllBytes(normal));
                CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(broken));
                Assert.AreEqual(2, progress.SourceProcessedCount);
                Assert.AreEqual(1, dialogs.Messages.Count);
                StringAssert.Contains(dialogs.Messages.Single(), broken);
                Assert.IsTrue(diagnosticAttempts > 0);
            }
            finally { CleanupCreatedExpansionPaths(roots); }
        });
    }

    /// <summary>当該ケースが作成した展開先だけを回収し、回収失敗をテスト失敗として残します。</summary>
    private static void CleanupCreatedExpansionPaths(IEnumerable<string> paths)
    {
        List<Exception> failures = [];
        foreach (string path in paths)
        {
            TempDirectoryPublisher.TryDeleteManagedPath(path,
                logWarning: (failedPath, error) => failures.Add(new IOException("展開先の回収失敗: " + failedPath, error)));
            if (Directory.Exists(path) || File.Exists(path))
            {
                failures.Add(new IOException("展開先が回収後も残っています: " + path));
            }
        }
        if (failures.Count != 0) { throw new AggregateException(failures); }
    }

    /// <summary>
    /// 親参照・絶対/drive/UNC・ADS・末尾dot/space・通常分類でfileとなる . の実ZIPを拒否し、展開先外の専有sentinelとpath集合を保つことを確認する。
    /// </summary>
    /// <remarks>実ファイルの存在・内容と物理操作の成否を観測するため独立した作業フォルダを用いる。</remarks>
    [TestMethod]
    [DataRow("../outside.txt")]
    [DataRow(".. /outside.txt")]
    [DataRow(" ../outside.txt")]
    [DataRow("sub/../outside.txt")]
    [DataRow("absolute")]
    [DataRow("drive-relative")]
    [DataRow(@"\\ingress-invalid-host\share\outside.txt")]
    [DataRow("chart.bms:stream")]
    [DataRow("name./outside.txt")]
    [DataRow("name /outside.txt")]
    [DataRow(".")]
    public void SevenZipArchiveExtractor_RejectsEntryOutsideExtractionDirectory(string entryName)
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string outside = Path.Combine(tempDirectoryPath, "outside.txt");
            File.WriteAllText(outside, "owned-sentinel");
            entryName = entryName switch
            {
                "absolute" => outside,
                "drive-relative" => outside[..2] + "outside.txt",
                _ => entryName
            };
            string archivePath = Path.Combine(tempDirectoryPath, "unsafe.zip");
            using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                archive.CreateEntry(entryName);
            }
            string nativeLibrary = SevenZipArchiveExtractor.ResolveBundledSevenZipLibraryPath();
            if (entryName == ".")
            {
                using var reference = new SevenZipExtractor.ArchiveFile(archivePath, nativeLibrary);
                Assert.AreEqual(".", reference.Entries.Single().FileName);
                Assert.IsFalse(reference.Entries.Single().IsFolder, "directoryとしての解釈をfile拒否の保証へ読み替えません。");
            }
            string[] originalPaths = Directory.GetFileSystemEntries(tempDirectoryPath, "*", System.IO.SearchOption.AllDirectories);
            byte[] archiveBytes = File.ReadAllBytes(archivePath);
            string extractionDirectoryPath = Path.Combine(tempDirectoryPath, "extracted");
            Assert.ThrowsExactly<InvalidDataException>(() => SevenZipArchiveExtractor.ExtractArchiveEntries(
                archivePath, nativeLibrary, extractionDirectoryPath));
            Assert.AreEqual("owned-sentinel", File.ReadAllText(outside));
            CollectionAssert.AreEqual(archiveBytes, File.ReadAllBytes(archivePath));
            CollectionAssert.AreEquivalent(originalPaths, Directory.GetFileSystemEntries(tempDirectoryPath, "*", System.IO.SearchOption.AllDirectories));
            Assert.IsFalse(Directory.Exists(extractionDirectoryPath));
        });
    }

    private static string GetArchiveFixturePath(string fileName)
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "archives", fileName);
    }

    private static DateTime GetExpectedArchiveLastWriteTime()
    {
        return new DateTime(2002, 1, 11, 18, 0, 8);
    }


    private sealed class ReentrantCleanupFileMutationService : IFileMutationService
    {
        private readonly RealFileMutationService inner = new();
        private BMSLibrary library = null!;
        private ChartPackage reentryPackage = null!;
        private Task reentryTask = null!;
        private int cleanupEntryObserved;

        internal bool ReentryCompletedDuringCleanup { get; private set; }

        internal Exception? ReentryFailure { get; private set; }

        internal PendingInstalledOnlyResourceOverwriteResult ReentryResult { get; private set; } = null!;

        internal void Configure(BMSLibrary library, ChartPackage reentryPackage)
        {
            this.library = library ?? throw new ArgumentNullException(nameof(library));
            this.reentryPackage = reentryPackage ?? throw new ArgumentNullException(nameof(reentryPackage));
        }

        internal void WaitForReentryCompletion()
        {
            reentryTask?.GetAwaiter().GetResult();
        }

        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null!)
            => inner.EnsureDirectory(directoryPath, options);

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
            => inner.MoveFile(sourcePath, destinationPath, overwrite, options);

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
            => inner.MoveDirectory(sourcePath, destinationPath, overwrite, options);

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
            => inner.CopyFile(sourcePath, destinationPath, overwrite, options);

        public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
            => inner.CopyDirectory(sourcePath, destinationPath, overwrite, options);

        public void DeleteFileDirect(string filePath, FileMutationOptions options = null!)
        {
            ObserveCleanupEntry();
            inner.DeleteFileDirect(filePath, options);
        }

        public void DeleteFileShell(string filePath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            ObserveCleanupEntry();
            inner.DeleteFileShell(filePath, uiOption, recycleOption, options);
        }

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions options = null!)
        {
            ObserveCleanupEntry();
            inner.DeleteDirectoryDirect(directoryPath, recursive, options);
        }

        public void DeleteDirectoryShell(string directoryPath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            ObserveCleanupEntry();
            inner.DeleteDirectoryShell(directoryPath, uiOption, recycleOption, options);
        }

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null!)
            => inner.SetTimestamps(path, isDirectory, creationTime, lastWriteTime, options);

        private void ObserveCleanupEntry()
        {
            if (Interlocked.Exchange(ref cleanupEntryObserved, 1) != 0)
            {
                return;
            }

            reentryTask = ConcurrencyProbe.StartDedicated(() =>
            {
                try
                {
                    ReentryResult = library.OverwritePendingInstalledOnlyPackagesResources([reentryPackage]);
                }
                catch (Exception exception)
                {
                    ReentryFailure = exception;
                }
            });
            // 外側片付け中の再入がロックで停止する反例を、保持中の停止検出で判定する。製品側の経路内なので、その場では投げず
            // 完了の有無だけを保持し、例外はReentryFailureへ集めて呼出し側が回収後に表明する。
            ReentryCompletedDuringCleanup = ConcurrencyProbe.WaitCompletedWhileHeld(reentryTask);
        }
    }

    private sealed class FailingLastWriteFileMutationService : IFileMutationService
    {
        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null!)
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void DeleteFileDirect(string filePath, FileMutationOptions options = null!)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        public void DeleteFileShell(string filePath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            DeleteFileDirect(filePath, options);
        }

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions options = null!)
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive);
            }
        }

        public void DeleteDirectoryShell(string directoryPath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            DeleteDirectoryDirect(directoryPath, recursive: true, options);
        }

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null!)
        {
            if (lastWriteTime.HasValue && !isDirectory)
            {
                throw new IOException("required_last_write_restore_failure");
            }

            if (!lastWriteTime.HasValue)
            {
                return;
            }

            if (isDirectory)
            {
                Directory.SetLastWriteTime(path, lastWriteTime.Value);
            }
            else
            {
                File.SetLastWriteTime(path, lastWriteTime.Value);
            }
        }
    }

    private sealed class ThrowOnFirstPendingRenameDialogService : IBmsLibraryDialogService
    {
        public int CallCount { get; private set; }

        public bool LaterFailureWasObserved { get; private set; }

        public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            CallCount++;
            if (CallCount == 1)
            {
                throw new InvalidOperationException("first pending rename notification failed");
            }
            LaterFailureWasObserved = true;
            return defaultResult;
        }

        public void Post(IReadOnlyList<BMSLibrary.OperationDialogMessage> messages, Func<bool> shouldSuppress)
            => throw new InvalidOperationException("この確認の窓口へ、待たない投函は届きません。");
    }

    private sealed class FailFirstMoveFileMutationService(int failureCount) : IFileMutationService
    {
        private readonly RealFileMutationService inner = new();

        private int moveCount;

        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null!)
            => inner.EnsureDirectory(directoryPath, options);

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            if (Interlocked.Increment(ref moveCount) <= failureCount)
            {
                throw new IOException("pending invalid-extension move failed");
            }
            inner.MoveFile(sourcePath, destinationPath, overwrite, options);
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
            => inner.MoveDirectory(sourcePath, destinationPath, overwrite, options);

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
            => inner.CopyFile(sourcePath, destinationPath, overwrite, options);

        public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
            => inner.CopyDirectory(sourcePath, destinationPath, overwrite, options);

        public void DeleteFileDirect(string filePath, FileMutationOptions options = null!)
            => inner.DeleteFileDirect(filePath, options);

        public void DeleteFileShell(string filePath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
            => inner.DeleteFileShell(filePath, uiOption, recycleOption, options);

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions options = null!)
            => inner.DeleteDirectoryDirect(directoryPath, recursive, options);

        public void DeleteDirectoryShell(string directoryPath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
            => inner.DeleteDirectoryShell(directoryPath, uiOption, recycleOption, options);

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null!)
            => inner.SetTimestamps(path, isDirectory, creationTime, lastWriteTime, options);
    }

}
