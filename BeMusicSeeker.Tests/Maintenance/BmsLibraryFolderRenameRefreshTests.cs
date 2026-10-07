using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualBasic.FileIO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryFolderRenameRefreshTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RenameChartFolder_FailureDialogRunsAfterFilesystemAndOnlyOnce(bool reportAtTerminal)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_FolderFailureDeferred_" + Guid.NewGuid().ToString("N"));
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Source");
            Directory.CreateDirectory(sourceDirectoryPath);
            File.WriteAllText(Path.Combine(sourceDirectoryPath, "chart.bms"), "#PLAYER 1");
            List<string> phases = [];
            var fileMutationService = new TestFileMutationService
            {
                MoveDirectoryFailureSourcePath = sourceDirectoryPath,
                OperationObserver = phase => phases.Add(phase)
            };
            var dialogService = new RecordingDialogService(phases);
            try
            {
                var library = new TestBmsLibrary(
                    songDbPath,
                    null,
                    null,
                    fileMutationService,
                    dialogService);
                int baselineOwnedCollectionVersion = library.OwnedCollectionVersion;

                LibraryMutationSessionReceipt receipt = library.RenameChartFolderWithReceipt(
                    sourceDirectoryPath,
                    "Renamed",
                    reportAtTerminal: reportAtTerminal);

                Assert.IsNotNull(receipt);
                Assert.IsFalse(receipt.DurableCommit);
                Assert.IsNotNull(receipt.PhysicalFailure);
                Assert.AreEqual(sourceDirectoryPath, receipt.FailedTarget.SourcePath);
                Assert.AreEqual(Path.Combine(tempRootPath, "Renamed"), receipt.FailedTarget.DestinationPath);
                Assert.AreEqual(baselineOwnedCollectionVersion, library.OwnedCollectionVersion);
                Assert.AreEqual(reportAtTerminal ? 0 : 1, dialogService.CallCount);
                Assert.AreEqual(reportAtTerminal ? "filesystem" : "filesystem|dialog", string.Join("|", phases));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RenameChartFolder_DestinationExistsRetainsPreflightDialog(bool reportAtTerminal)
    {
        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.Combine(Path.GetDirectoryName(songDbPath)!, "preflight-" + Guid.NewGuid().ToString("N"));
            string source = Path.Combine(root, "source");
            string destination = Path.Combine(root, "destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            var dialogs = new RecordingDialogService();
            var mutations = new List<string>();
            try
            {
                var library = new TestBmsLibrary(songDbPath, null, null,
                    new TestFileMutationService { OperationObserver = mutations.Add }, dialogs);
                LibraryMutationSessionReceipt receipt = library.RenameChartFolderWithReceipt(source, "destination", reportAtTerminal: reportAtTerminal);
                Assert.IsNull(receipt);
                Assert.AreEqual(1, dialogs.CallCount);
                Assert.AreEqual(0, mutations.Count);
                Assert.IsTrue(Directory.Exists(source));
                Assert.IsTrue(Directory.Exists(destination));
            }
            finally { Directory.Delete(root, recursive: true); }
        });
    }

    [TestMethod]
    public void RenameBMSFilesExtensionsWithReceipt_MultipleExtensionFamiliesUseSingleSession()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.Combine(
                Path.GetDirectoryName(songDbPath)!,
                "extension-session-" + Guid.NewGuid().ToString("N"));
            string bSource = Path.Combine(root, "alpha.bme");
            string pSource = Path.Combine(root, "beta.pms");
            string bDestination = Path.Combine(root, "alpha.bmx");
            string pDestination = Path.Combine(root, "beta.pmx");
            Directory.CreateDirectory(root);
            File.WriteAllText(bSource, "#PLAYER 1\r\n#TITLE Alpha\r\n#BPM 120\r\n");
            File.WriteAllText(pSource, "#PLAYER 1\r\n#TITLE Beta\r\n#BPM 120\r\n");
            ChartFile bFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bSource));
            bFile = bFile with { Token = new OwnedChartToken() };
            ChartFile pFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pSource));
            pFile = pFile with { Token = new OwnedChartToken() };
            try
            {
                using (var db = new LR2SongDBExtended(songDbPath))
                {
                    db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(bFile), typeof(LR2SongDB.song));
                    db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(pFile), typeof(LR2SongDB.song));
                }
                var library = new TestBmsLibrary(
                    songDbPath,
                    null,
                    null,
                    new TestFileMutationService(),
                    new RecordingDialogService());
                OwnedChartCollectionTestSupport.SetLibraryFilesWithoutNotification(library, [bFile, pFile]);
                int ownedCollectionPublicationCount = 0;
                int normalRefreshPublicationCount = 0;
                library.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(BMSLibrary.OwnedCollectionVersion))
                    {
                        ownedCollectionPublicationCount++;
                    }
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        normalRefreshPublicationCount++;
                    }
                };

                LibraryMutationSessionReceipt receipt = library.RenameBMSFilesExtensionsWithReceipt(
                    [
                        library.PrepareLibraryFileExtensionRenameBatch(
                            [(bFile)],
                            ".bmx"),
                        library.PrepareLibraryFileExtensionRenameBatch(
                            [(pFile)],
                            ".pmx")
                    ],
                    unregister: false);

                Assert.IsTrue(receipt.DurableCommit);
                Assert.AreEqual(2, receipt.ConfirmedChangeCount);
                Assert.AreEqual(new LibraryMutationSessionApplyCounts
                {
                    CatalogApplyCount = 1,
                    PackageReferenceApplyCount = 1,
                    Lr2SyncCount = 1,
                    RequiredPublicationCount = 1
                }, receipt.ApplyCounts);
                Assert.AreEqual(2, receipt.CatalogChartPathChangeCount);
                Assert.IsNull(receipt.ApplyFailure);
                Assert.IsNull(receipt.FinalizationFailure);
                Assert.AreEqual(1, ownedCollectionPublicationCount);
                Assert.AreEqual(1, normalRefreshPublicationCount);
                Assert.IsFalse(File.Exists(bSource));
                Assert.IsFalse(File.Exists(pSource));
                Assert.IsTrue(File.Exists(bDestination));
                Assert.IsTrue(File.Exists(pDestination));
                using var readback = new LR2SongDBExtended(songDbPath);
                Assert.AreEqual(0, readback.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM song WHERE path IN (?, ?);",
                    bSource,
                    pSource));
                Assert.AreEqual(2, readback.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM song WHERE path IN (?, ?);",
                    bDestination,
                    pDestination));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public void RenameBMSFilesExtensionsWithReceipt_FilesystemFailuresStayInSessionForTerminalAggregation()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.Combine(
                Path.GetDirectoryName(songDbPath)!,
                "extension-session-failure-" + Guid.NewGuid().ToString("N"));
            string bSource = Path.Combine(root, "alpha.bme");
            string pSource = Path.Combine(root, "beta.pms");
            Directory.CreateDirectory(root);
            File.WriteAllText(bSource, "#PLAYER 1\r\n#TITLE Alpha\r\n#BPM 120\r\n");
            File.WriteAllText(pSource, "#PLAYER 1\r\n#TITLE Beta\r\n#BPM 120\r\n");
            ChartFile bFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bSource));
            bFile = bFile with { Token = new OwnedChartToken() };
            ChartFile pFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pSource));
            pFile = pFile with { Token = new OwnedChartToken() };
            var mutations = new TestFileMutationService();
            mutations.MoveFileFailureSourcePaths.Add(bSource);
            mutations.MoveFileFailureSourcePaths.Add(pSource);
            var dialogs = new RecordingDialogService();
            try
            {
                var library = new TestBmsLibrary(songDbPath, null, null, mutations, dialogs);
                OwnedChartCollectionTestSupport.SetLibraryFilesWithoutNotification(library, [bFile, pFile]);

                LibraryMutationSessionReceipt receipt = library.RenameBMSFilesExtensionsWithReceipt(
                    [
                        library.PrepareLibraryFileExtensionRenameBatch(
                            [(bFile)],
                            ".bmx"),
                        library.PrepareLibraryFileExtensionRenameBatch(
                            [(pFile)],
                            ".pmx")
                    ],
                    unregister: false);

                Assert.IsFalse(receipt.DurableCommit);
                Assert.AreEqual(0, receipt.ConfirmedChangeCount);
                Assert.AreEqual(2, receipt.ItemFailures.Count);
                Assert.AreEqual(bSource, receipt.ItemFailures[0].Target.SourcePath);
                Assert.AreEqual(pSource, receipt.ItemFailures[1].Target.SourcePath);
                Assert.AreEqual(0, dialogs.CallCount,
                    "The receipt-returning route must defer item failures to the workflow terminal.");
                Assert.IsTrue(File.Exists(bSource));
                Assert.IsTrue(File.Exists(pSource));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public async Task RenameChartFolder_UpdatesFolderCellWithoutStorageRowCollectionNotification()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        await WithTemporarySongDbAsync(async delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RenameRefresh_" + Guid.NewGuid().ToString("N"));
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Source");
            string chartPath = Path.Combine(sourceDirectoryPath, "chart.bms");
            Directory.CreateDirectory(sourceDirectoryPath);
            File.WriteAllText(chartPath, "#PLAYER 1");
            try
            {
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                ChartFile file = ChartTestValues.Empty() with
                {
                    Path = chartPath
                };
                file = file with { Token = new OwnedChartToken() };
                file = file with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                int bmsFilesChangedCount = 0;
                int folderChangedCount = 0;
                int pathChangedCount = 0;
                var bmsFilesPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var folderChanged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var pathChanged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                library.PropertyChanged += delegate (object? sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        Interlocked.Increment(ref bmsFilesChangedCount);
                        bmsFilesPublished.TrySetResult(true);
                    }
                };
                library.BmsCharts = [file];
                // 初期の共通通知を待ってから、操作の通知を捕捉します。
                await bmsFilesPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                file = library.BmsCharts.Single(chart => chart.Path == file.Path);
                Interlocked.Exchange(ref bmsFilesChangedCount, 0);
                library.DuplicateChartGroups = [];
                int baselineOwnedCollectionVersion = library.OwnedCollectionVersion;
                int baselineParentFolderVersion = library.BMSParentFolderListCacheVersion;
                int baselineDuplicateInvalidationVersion = library.DuplicateChartGroupsInvalidationVersion;
                int ownedCollectionVersionChangedCount = 0;
                int parentFolderVersionChangedCount = 0;
                bool stateAvailableAtOwnedCollectionNotification = false;
                library.PropertyChanged += delegate (object? sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSLibrary.OwnedCollectionVersion))
                    {
                        Interlocked.Increment(ref ownedCollectionVersionChangedCount);
                        ChartFile current = library.BmsCharts.Single(chart => ReferenceEquals(chart.Token, file.Token));
                        stateAvailableAtOwnedCollectionNotification = string.Equals(current.Folder, "Renamed", StringComparison.Ordinal)
                            && current.Path.Contains(Path.Combine("Renamed", "chart.bms"), StringComparison.OrdinalIgnoreCase);
                    }
                    if (e.PropertyName == nameof(BMSLibrary.BMSParentFolderListCacheVersion))
                    {
                        Interlocked.Increment(ref parentFolderVersionChangedCount);
                    }
                };
                ChartFile capturedBeforeRename = file;
                int handledVersion = library.NormalLibraryRefreshNotificationVersion;
                library.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName != nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        return;
                    }
                    NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledVersion);
                    ChartFile? changed = batch.ChangedCharts.SingleOrDefault(chart => ReferenceEquals(chart.Token, capturedBeforeRename.Token));
                    if (changed is null)
                    {
                        return;
                    }
                    file = changed;
                    if (changed.Folder != capturedBeforeRename.Folder)
                    {
                        Interlocked.Increment(ref folderChangedCount);
                        folderChanged.TrySetResult(true);
                    }
                    if (changed.Path != capturedBeforeRename.Path)
                    {
                        Interlocked.Increment(ref pathChangedCount);
                        pathChanged.TrySetResult(true);
                    }
                };

                library.RenameChartFolder(sourceDirectoryPath, "Renamed");

                await Task.WhenAll(folderChanged.Task, pathChanged.Task).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreEqual(1, Volatile.Read(ref bmsFilesChangedCount));
                Assert.AreEqual(1, Volatile.Read(ref folderChangedCount));
                Assert.AreEqual(1, Volatile.Read(ref pathChangedCount));
                Assert.AreEqual("Renamed", CurrentOwnedChart(library, file).Folder);
                Assert.IsTrue(CurrentOwnedChart(library, file).Path.Contains(Path.Combine("Renamed", "chart.bms")));
                Assert.AreEqual(baselineOwnedCollectionVersion + 1, library.OwnedCollectionVersion);
                Assert.AreEqual(1, Volatile.Read(ref ownedCollectionVersionChangedCount));
                Assert.IsTrue(stateAvailableAtOwnedCollectionNotification);
                Assert.AreEqual(baselineParentFolderVersion + 1, library.BMSParentFolderListCacheVersion);
                Assert.AreEqual(1, Volatile.Read(ref parentFolderVersionChangedCount));
                Assert.IsNull(library.DuplicateChartGroups);
                Assert.AreEqual(baselineDuplicateInvalidationVersion + 1, library.DuplicateChartGroupsInvalidationVersion);
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public void RenameChartFolder_PublishesAfterDurableFinalizerAndLeaseRelease()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RenamePublicationBoundary_" + Guid.NewGuid().ToString("N"));
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Source");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Renamed");
            string chartPath = Path.Combine(sourceDirectoryPath, "chart.bms");
            string destinationChartPath = Path.Combine(destinationDirectoryPath, "chart.bms");
            Directory.CreateDirectory(sourceDirectoryPath);
            File.WriteAllText(chartPath, "#PLAYER 1");
            try
            {
                var library = new TestBmsLibrary(
                    songDbPath,
                    null,
                    null,
                    new TestFileMutationService(),
                    new RecordingDialogService());
                ChartFile file = (ChartTestValues.Empty() with { Path = chartPath });
                file = file with { Token = new OwnedChartToken() };
                file = file with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                library.BmsCharts = [file];

                int publicationCount = 0;
                bool publicationObservedFinalizedFilesystem = false;
                bool publicationObservedReleasedLease = false;
                library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
                {
                    if (args.PropertyName != nameof(BMSLibrary.OwnedCollectionVersion))
                    {
                        return;
                    }
                    Interlocked.Increment(ref publicationCount);
                    publicationObservedFinalizedFilesystem = !Directory.Exists(sourceDirectoryPath)
                        && Directory.Exists(destinationDirectoryPath)
                        && string.Equals(CurrentOwnedChart(library, file).Path, destinationChartPath, StringComparison.OrdinalIgnoreCase);
                    using LibraryFileMutationLease probe = library.TryBeginLibraryFileMutation(
                        "normal_rename_publication_probe");
                    publicationObservedReleasedLease = probe != null;
                };

                LibraryMutationSessionReceipt receipt = library.RenameChartFolderWithReceipt(
                    sourceDirectoryPath,
                    "Renamed");

                Assert.IsNotNull(receipt);
                Assert.IsTrue(receipt.DurableCommit);
                Assert.AreEqual(1, Volatile.Read(ref publicationCount));
                Assert.IsTrue(publicationObservedFinalizedFilesystem);
                Assert.IsTrue(publicationObservedReleasedLease);
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public void RenameChartFolder_Lr2FinalizationFailureKeepsDurableStateWithoutSuccessPublication()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(
                Path.GetTempPath(),
                "BeMusicSeeker_RenameDurableFinalizationFailure_" + Guid.NewGuid().ToString("N"));
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Source");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "PackFinalizationFailure");
            string sourceChartPath = Path.Combine(sourceDirectoryPath, "chart.bms");
            string destinationChartPath = Path.Combine(destinationDirectoryPath, "chart.bms");
            string lr2RootPath = Path.Combine(tempRootPath, "LR2beta3");
            Directory.CreateDirectory(sourceDirectoryPath);
            File.WriteAllText(sourceChartPath, "#PLAYER 1\r\n#TITLE Durable finalization failure\r\n");
            try
            {
                ChartFile file = (ChartTestValues.Empty() with { Path = sourceChartPath });
                file = file with { Token = new OwnedChartToken() };
                file = file with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                LR2Config lr2Config = BmsPlaylistTestSupport.CreateLr2Config(lr2RootPath, tempRootPath);
                var library = new TestBmsLibrary(
                    songDbPath,
                    () => lr2Config,
                    null,
                    new TestFileMutationService(),
                    new RecordingDialogService(),
                    new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    () => new BmsLibraryOptionsSnapshot
                    {
                        OperationModeLR2DB = true,
                        LR2RootPath = lr2RootPath
                    })
                {
                    BmsCharts = [file]
                };
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                    songDb.Execute(
                        "CREATE TRIGGER fail_lr2_folder_insert BEFORE INSERT ON folder WHEN NEW.path LIKE '%PackFinalizationFailure%' "
                        + "BEGIN SELECT RAISE(ABORT, 'forced durable finalization failure'); END;");
                }

                int ownedCollectionPublicationCount = 0;
                int normalRefreshPublicationCount = 0;
                library.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(BMSLibrary.OwnedCollectionVersion))
                    {
                        ownedCollectionPublicationCount++;
                    }
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        normalRefreshPublicationCount++;
                    }
                };

                LibraryMutationSessionReceipt receipt = library.RenameChartFolderWithReceipt(
                    sourceDirectoryPath,
                    "PackFinalizationFailure");

                Assert.IsNotNull(receipt);
                Assert.IsTrue(receipt.DurableCommit);
                Assert.IsTrue(receipt.HasDurableFinalizationFailure);
                Assert.IsNotNull(receipt.ApplyFailure);
                Assert.AreSame(receipt.ApplyFailure, receipt.PrimaryFailure);
                Assert.IsNull(receipt.CleanupFailure);
                CollectionAssert.Contains(receipt.CandidatePaths.ToArray(), sourceDirectoryPath);
                CollectionAssert.Contains(receipt.CandidatePaths.ToArray(), destinationDirectoryPath);
                Assert.IsFalse(Directory.Exists(sourceDirectoryPath));
                Assert.IsTrue(File.Exists(destinationChartPath));
                Assert.AreEqual(destinationChartPath, CurrentOwnedChart(library, file).Path);
                Assert.AreEqual(0, ownedCollectionPublicationCount);
                Assert.AreEqual(0, normalRefreshPublicationCount);
                using var verifySongDb = new LR2SongDBExtended(songDbPath);
                Assert.IsNotNull(verifySongDb.Find<LR2SongDB.song>(destinationChartPath));
                Assert.IsNull(verifySongDb.Find<LR2SongDB.song>(sourceChartPath));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    /// <summary>実フォルダ変更後のBMS範囲事実だけを局所取得し、背景件数に比例した全走査を行わないことを確認します。</summary>
    [DataTestMethod]
    [DataRow(16, false)]
    [DataRow(16, true)]
    public void RenameIngress_CapturesOnlyLocalBmsRangeFacts(int backgroundChartCount, bool autoRename)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(
                Path.GetTempPath(),
                "BeMusicSeeker_LocalBmsScopeCapture_" + Guid.NewGuid().ToString("N"));
            string libraryRootPath = Path.Combine(tempRootPath, "LibraryRoot");
            string firstSourceDirectoryPath = Path.Combine(libraryRootPath, "TargetSource1");
            string secondSourceDirectoryPath = Path.Combine(libraryRootPath, "TargetSource2");
            string backgroundDirectoryPath = Path.Combine(libraryRootPath, "Background");
            string firstTargetChartPath = Path.Combine(firstSourceDirectoryPath, "target-1.bms");
            string secondTargetChartPath = Path.Combine(secondSourceDirectoryPath, "target-2.bms");
            string lr2RootPath = Path.Combine(tempRootPath, "LR2beta3");
            Directory.CreateDirectory(firstSourceDirectoryPath);
            Directory.CreateDirectory(secondSourceDirectoryPath);
            Directory.CreateDirectory(backgroundDirectoryPath);
            File.WriteAllText(
                firstTargetChartPath,
                "#PLAYER 1\r\n#TITLE Target Title 1\r\n#ARTIST Target Artist 1\r\n");
            File.WriteAllText(
                secondTargetChartPath,
                "#PLAYER 1\r\n#TITLE Target Title 2\r\n#ARTIST Target Artist 2\r\n");
            try
            {
                ChartFile firstTarget = (ChartTestValues.Empty() with { Path = firstTargetChartPath });
                firstTarget = firstTarget with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                firstTarget = firstTarget with { Title = "Target Title 1", RawTitle = "Target Title 1" };
                firstTarget = firstTarget with { Artist = "Target Artist 1", RawArtist = "Target Artist 1" };
                firstTarget = firstTarget with { Favorite = 1 };
                ChartFile secondTarget = (ChartTestValues.Empty() with { Path = secondTargetChartPath });
                secondTarget = secondTarget with { Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" };
                secondTarget = secondTarget with { Title = "Target Title 2", RawTitle = "Target Title 2" };
                secondTarget = secondTarget with { Artist = "Target Artist 2", RawArtist = "Target Artist 2" };
                secondTarget = secondTarget with { Favorite = 0 };
                var files = new List<ChartFile> { firstTarget, secondTarget };
                for (int index = 0; index < backgroundChartCount; index++)
                {
                    ChartFile background = ChartTestValues.Empty() with
                    {
                        Path = Path.Combine(backgroundDirectoryPath, $"background-{index:D3}.bms")
                    };
                    background = background with { Md5 = index.ToString("x32") };
                    files.Add(background);
                }

                LR2Config lr2Config = BmsPlaylistTestSupport.CreateLr2Config(lr2RootPath, libraryRootPath);
                var library = new TestBmsLibrary(
                    songDbPath,
                    () => lr2Config,
                    null,
                    new TestFileMutationService(),
                    new RecordingDialogService(),
                    new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    () => new BmsLibraryOptionsSnapshot
                    {
                        OperationModeLR2DB = true,
                        LR2RootPath = lr2RootPath,
                        FolderNameFormat = "[%ARTIST%] %TITLE%"
                    })
                {
                    BmsCharts = files
                };
                // warm操作前のfixture seedは本番保存契約を検証しないため、一つのtransactionにまとめて共通DBロックの保持時間を短縮する。
                BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
                {
                    foreach (ChartFile file in files)
                    {
                        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                    }
                });

                BMSLibrary.InstalledPrimaryHashWarmupResult initialPrimary =
                    library.WarmInstalledPrimaryHashLookup("配置変更初期primary");
                Assert.IsFalse(initialPrimary.FullDirectoryLookupInitialized);
                OwnedChartHashIndexVersionedSnapshot initialHash = library.GetOwnedChartHashIndexSnapshot();
                InstalledChartLookupIndexSnapshot initialInstalled = InvokeCreateInstalledChartLookupSnapshot(library);
                PlaylistLibraryResolveIndexSnapshot initialPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                    CancellationToken.None,
                    out bool initialPlaylistCacheHit,
                    out int initialPlaylistStaleRetries);
                Assert.IsFalse(initialPlaylistCacheHit);
                Assert.AreEqual(0, initialPlaylistStaleRetries);
                Assert.IsTrue(initialHash.ContainsMd5(firstTarget.Md5));
                Assert.IsTrue(initialHash.ContainsMd5(secondTarget.Md5));
                Assert.IsTrue(initialInstalled.ContainsPrimaryHash(firstTarget.Md5));
                Assert.IsTrue(initialInstalled.ContainsPrimaryHash(secondTarget.Md5));
                Assert.IsTrue(initialPlaylist.ContainsCandidate(ChartFileKind.Bms, firstTarget.Path));
                Assert.IsTrue(initialPlaylist.ContainsCandidate(ChartFileKind.Bms, secondTarget.Path));

                List<string> hashWork = [];
                List<string> playlistWork = [];
                List<string> installedWork = [];
                library.OwnedChartHashIndexStoreWorkObserver = hashWork.Add;
                library.PlaylistLibraryResolveIndexStoreWorkObserver = playlistWork.Add;
                library.InstalledChartLookupStoreWorkObserver = installedWork.Add;

                (ChartFile Target, string SourceDirectory, string DestinationName, int Favorite)[] operations =
                {
                    (firstTarget, firstSourceDirectoryPath, autoRename
                        ? "[Target Artist 1] Target Title 1"
                        : "Renamed1", 1),
                    (secondTarget, secondSourceDirectoryPath, autoRename
                        ? "[Target Artist 2] Target Title 2"
                        : "Renamed2", 0)
                };
                BMSLibrary.OwnedAdjacentIndexWarmupResult before = library.WarmOwnedRealPathDirectoryView("配置変更warmup前");
                foreach ((ChartFile target, string sourceDirectoryPath, string destinationName, int favorite) in operations)
                {
                    string oldChartPath = target.Path;
                    if (autoRename)
                    {
                        AutoRenameBatchResult result = library.AutoRenameChartFoldersWithProgress(
                            [(target)], false, new RecordingFolderAutoRenameProgressWriter());
                        Assert.IsTrue(result.HasDurableCommit);
                    }
                    else
                    {
                        LibraryMutationSessionReceipt receipt = library.RenameChartFolderWithReceipt(
                            sourceDirectoryPath,
                            destinationName);
                        Assert.IsNotNull(receipt);
                        Assert.IsTrue(receipt.DurableCommit);
                    }

                    BMSLibrary.OwnedAdjacentIndexWarmupResult after = library.WarmOwnedRealPathDirectoryView("配置変更warmup後");
                    int countQueryDelta = after.BmsCountQueryCount - before.BmsCountQueryCount;
                    int rangeQueryDelta = after.BmsRangeQueryCount - before.BmsRangeQueryCount;
                    int visitedReferenceDelta =
                        after.BmsRangeVisitedReferenceCount - before.BmsRangeVisitedReferenceCount;
                    int returnedPathDelta = after.BmsRangeReturnedPathCount - before.BmsRangeReturnedPathCount;
                    Assert.IsTrue(countQueryDelta > 0, "配置変更ごとにBMS祖先件数queryを実行すること。");
                    Assert.IsTrue(rangeQueryDelta > 0, "配置変更ごとにBMS範囲queryを実行すること。");
                    Assert.IsTrue(returnedPathDelta > 0, "対象BMSのexact pathを配置変更receiptへ保持すること。");
                    Assert.IsTrue(
                        visitedReferenceDelta <= 1,
                        $"対象1譜面の局所範囲queryが訪問したref数は1以下であること（実測{visitedReferenceDelta}、背景{backgroundChartCount}）。");
                    before = after;

                    string destinationDirectoryPath = Path.Combine(libraryRootPath, destinationName);
                    string destinationChartPath = Path.Combine(destinationDirectoryPath, Path.GetFileName(oldChartPath));
                    Assert.IsFalse(Directory.Exists(sourceDirectoryPath));
                    Assert.IsTrue(File.Exists(destinationChartPath));
                    Assert.AreEqual(destinationChartPath, library.BmsCharts.Single(chart => chart.Md5 == target.Md5).Path);

                    // ここはSELECT専用の観測なので、writer接続を保持せずread-only入口を使う。
                    using (LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
                    {
                        string[] dbPaths = verifySongDb.Table<LR2SongDB.song>().Select(row => row.path).ToArray();
                        Assert.IsFalse(dbPaths.Contains(oldChartPath, StringComparer.OrdinalIgnoreCase));
                        Assert.IsTrue(dbPaths.Contains(destinationChartPath, StringComparer.OrdinalIgnoreCase));
                        LR2SongDB.song row = verifySongDb.Table<LR2SongDB.song>().Single(
                            candidate => string.Equals(candidate.path, destinationChartPath, StringComparison.OrdinalIgnoreCase));
                        Assert.AreEqual((int?)favorite, row.favorite);
                        Assert.AreEqual(files.Count, library.BmsCharts.Count);
                        foreach (ChartFile file in library.BmsCharts)
                        {
                            Assert.IsTrue(
                                dbPaths.Contains(file.Path, StringComparer.OrdinalIgnoreCase),
                                "配置変更後も対象と未対象のsong rowを保持します。");
                        }
                    }

                    OwnedChartHashIndexVersionedSnapshot updatedHash = library.GetOwnedChartHashIndexSnapshot();
                    OwnedChartHashIndexVersionedSnapshot cachedHash = library.GetOwnedChartHashIndexSnapshot();
                    InstalledChartLookupIndexSnapshot updatedInstalled = InvokeCreateInstalledChartLookupSnapshot(library);
                    InstalledChartLookupIndexSnapshot cachedInstalled = InvokeCreateInstalledChartLookupSnapshot(library);
                    BMSLibrary.InstalledPrimaryHashWarmupResult updatedPrimary =
                        library.WarmInstalledPrimaryHashLookup("配置変更primary");
                    BMSLibrary.InstalledPrimaryHashWarmupResult cachedPrimary =
                        library.WarmInstalledPrimaryHashLookup("配置変更primary");
                    PlaylistLibraryResolveIndexSnapshot updatedPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                        CancellationToken.None,
                        out bool updatedPlaylistCacheHit,
                        out int updatedPlaylistStaleRetries);
                    PlaylistLibraryResolveIndexSnapshot cachedPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                        CancellationToken.None,
                        out bool cachedPlaylistCacheHit,
                        out int cachedPlaylistStaleRetries);

                    Assert.AreSame(updatedHash, cachedHash);
                    Assert.AreEqual(initialHash.Version, updatedHash.Version);
                    Assert.AreEqual(initialHash.Md5Count, updatedHash.Md5Count);
                    Assert.AreEqual(initialHash.Sha256Count, updatedHash.Sha256Count);
                    Assert.IsTrue(updatedHash.ContainsMd5(firstTarget.Md5));
                    Assert.IsTrue(updatedHash.ContainsMd5(secondTarget.Md5));
                    Assert.AreSame(updatedInstalled, cachedInstalled);
                    Assert.IsTrue(updatedInstalled.ContainsPrimaryHash(target.Md5));
                    Assert.IsFalse(updatedInstalled.GetDistinctDirectoriesByPrimaryHash(target.Md5)
                        .Contains(sourceDirectoryPath, StringComparer.OrdinalIgnoreCase));
                    Assert.IsTrue(updatedInstalled.GetDistinctDirectoriesByPrimaryHash(target.Md5)
                        .Contains(destinationDirectoryPath, StringComparer.OrdinalIgnoreCase));
                    Assert.AreEqual("cached", updatedPrimary.Status);
                    Assert.AreEqual(0L, updatedPrimary.BuildMs);
                    Assert.AreEqual("cached", cachedPrimary.Status);
                    Assert.AreEqual(0L, cachedPrimary.BuildMs);
                    Assert.IsTrue(updatedPrimary.FullDirectoryLookupInitialized);
                    Assert.IsTrue(updatedPlaylistCacheHit);
                    Assert.AreEqual(0, updatedPlaylistStaleRetries);
                    Assert.IsTrue(cachedPlaylistCacheHit);
                    Assert.AreEqual(0, cachedPlaylistStaleRetries);
                    Assert.AreSame(updatedPlaylist, cachedPlaylist);
                    Assert.IsFalse(updatedPlaylist.ContainsCandidate(ChartFileKind.Bms, oldChartPath));
                    Assert.IsTrue(updatedPlaylist.ContainsCandidate(ChartFileKind.Bms, destinationChartPath));
                    Assert.IsTrue(initialInstalled.ContainsPrimaryHash(target.Md5));
                    Assert.IsTrue(initialInstalled.GetDistinctDirectoriesByPrimaryHash(target.Md5)
                        .Contains(sourceDirectoryPath, StringComparer.OrdinalIgnoreCase));
                    Assert.IsTrue(initialPlaylist.ContainsCandidate(ChartFileKind.Bms, oldChartPath));
                    Assert.IsFalse(initialPlaylist.ContainsCandidate(ChartFileKind.Bms, destinationChartPath));
                }

                Assert.IsTrue(
                    hashWork.Count(operation => operation == "owned_hash_source_enumeration") == 0,
                    "配置変更後のowned hash getterがsource全体を再列挙しました。");
                Assert.IsTrue(
                    playlistWork.Count(operation => operation == "playlist_resolve_source_enumeration" || operation == "playlist_resolve_full_root_enumeration") == 0,
                    "配置変更後のplaylist resolve getterがsource全体を再列挙しました。");
                Assert.IsTrue(
                    installedWork.Count(operation => operation == "installed_primary_hash_count_update") <= 8,
                    "配置変更後にinstalled lookupを全件再構築しました。");
                Assert.IsTrue(
                    installedWork.Count(operation => operation == "installed_primary_hash_count_update") > 0,
                    "実配置変更のinstalled lookup差分更新を観測できませんでした。");
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public void AutoRenameChartFolders_BatchesMultipleFolderMutationsIntoOneRefresh()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_AutoRenameBatch_" + Guid.NewGuid().ToString("N"));
            string libraryRootPath = Path.Combine(tempRootPath, "LibraryRoot");
            string firstDirectoryPath = Path.Combine(libraryRootPath, "FirstSource");
            string secondDirectoryPath = Path.Combine(libraryRootPath, "SecondSource");
            string firstChartPath = Path.Combine(firstDirectoryPath, "first.bms");
            string secondChartPath = Path.Combine(secondDirectoryPath, "second.bms");
            Directory.CreateDirectory(firstDirectoryPath);
            Directory.CreateDirectory(secondDirectoryPath);
            File.WriteAllText(firstChartPath, "#PLAYER 1\r\n#TITLE First Title\r\n#ARTIST First Artist\r\n#WAVAA first.wav\r\n#00111:AA\r\n");
            File.WriteAllText(secondChartPath, "#PLAYER 1\r\n#TITLE Second Title\r\n#ARTIST Second Artist\r\n");
            File.WriteAllText(Path.Combine(firstDirectoryPath, "first.wav"), "audio");
            try
            {
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
                {
                    SearchTargets = [libraryRootPath]
                };
                ChartFile firstFile = ChartTestValues.Empty() with
                {
                    Path = firstChartPath
                };
                firstFile = firstFile with { Token = new OwnedChartToken() };
                firstFile = firstFile with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                firstFile = firstFile with { Title = "First Title", RawTitle = "First Title" };
                firstFile = firstFile with { Artist = "First Artist", RawArtist = "First Artist" };
                ChartFile secondFile = ChartTestValues.Empty() with
                {
                    Path = secondChartPath
                };
                secondFile = secondFile with { Token = new OwnedChartToken() };
                secondFile = secondFile with { Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" };
                secondFile = secondFile with { Title = "Second Title", RawTitle = "Second Title" };
                secondFile = secondFile with { Artist = "Second Artist", RawArtist = "Second Artist" };
                library.BmsCharts = [firstFile, secondFile];
                var replacementIndex = new LibraryResourceIndex();
                replacementIndex.DirectoryLookupCache.AddDir(firstDirectoryPath, new[] { "first.wav" });
                replacementIndex.DirectoryLookupCache.AddDir(secondDirectoryPath, new[] { "second.wav" });
                LibraryResourceIndexOwner resourceIndexOwner = GetLibraryResourceIndexOwner(library);
                resourceIndexOwner.Replace(replacementIndex);
                List<(int Total, int Processed, string Path)> progress = [];
                int refreshCount = 0;
                library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
                {
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        Interlocked.Increment(ref refreshCount);
                    }
                };
                int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;

                AutoRenameBatchResult result = library.AutoRenameChartFoldersWithProgress([
                    (firstFile),
                    (secondFile)
                ], false, new RecordingFolderAutoRenameProgressWriter(update => progress.Add((update.TotalCount, update.ProcessedCount, update.CurrentPath))));
                NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

                Assert.AreEqual(1, Volatile.Read(ref refreshCount));
                Assert.AreEqual(new LibraryMutationSessionApplyCounts
                {
                    CatalogApplyCount = 1,
                    PackageReferenceApplyCount = 1,
                    ReverseLookupApplyCount = 1,
                    Lr2SyncCount = 1,
                    RequiredPublicationCount = 1
                }, result.SessionReceipt.ApplyCounts);
                Assert.IsTrue(batch.ChangedCharts.Count > 0);
                Assert.AreEqual(0, batch.DeletedTokens.Count);
                Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
                Assert.IsFalse(batch.ResetsPriorNotifications);
                Assert.AreEqual(2, progress.Last().Processed);
                Assert.IsTrue(progress.All(item => item.Total == 2));
                Assert.AreEqual(secondDirectoryPath, progress.Last().Path);
                Assert.AreEqual(Path.Combine(libraryRootPath, "[First Artist] First Title", "first.bms"), CurrentOwnedChart(library, firstFile).Path);
                Assert.AreEqual(Path.Combine(libraryRootPath, "[Second Artist] Second Title", "second.bms"), CurrentOwnedChart(library, secondFile).Path);
                LibraryResourceIndexSnapshot resourceSnapshot = resourceIndexOwner.CaptureSnapshot();
                Assert.IsNull(resourceSnapshot.DirectoryLookupCache.GetEntryOrNull(firstDirectoryPath));
                Assert.IsNull(resourceSnapshot.DirectoryLookupCache.GetEntryOrNull(secondDirectoryPath));
                Assert.IsNotNull(resourceSnapshot.DirectoryLookupCache.GetEntryOrNull(Path.Combine(libraryRootPath, "[First Artist] First Title")));
                Assert.IsNotNull(resourceSnapshot.DirectoryLookupCache.GetEntryOrNull(Path.Combine(libraryRootPath, "[Second Artist] Second Title")));
                Assert.IsFalse(Directory.Exists(firstDirectoryPath));
                Assert.IsFalse(Directory.Exists(secondDirectoryPath));

                string pendingDirectoryPath = Path.Combine(tempRootPath, "PendingAfterRename");
                string pendingChartPath = Path.Combine(pendingDirectoryPath, "pending.bms");
                Directory.CreateDirectory(pendingDirectoryPath);
                File.WriteAllText(
                    pendingChartPath,
                    "#PLAYER 1\r\n#TITLE First Title\r\n#ARTIST First Artist\r\n#WAVAA first.wav\r\n#00111:AA\r\n");
                ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage(
                    [BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingChartPath))]);
                pendingPackage.path = pendingDirectoryPath;
                pendingPackage.delete_parent = false;
                library.ChartPackagesPending = CreatePackageCollection([pendingPackage]);

                library.SearchEstimatedInstallationDirectory(pendingPackage);

                PackageChartEntry pendingEntry = pendingPackage.ChartEntries.Single();
                Assert.AreEqual(
                    Path.Combine(libraryRootPath, "[First Artist] First Title"),
                    pendingEntry.Chart.InstallDestination);
                Assert.AreNotEqual(firstDirectoryPath, pendingEntry.Chart.InstallDestination);

                string pendingBmsonPath = Path.Combine(pendingDirectoryPath, "pending.bmson");
                File.WriteAllText(
                    pendingBmsonPath,
                    "{\"version\":\"1.0.0\",\"info\":{\"title\":\"First Title\",\"artist\":\"First Artist\",\"mode_hint\":\"beat-7k\"},"
                    + "\"sound_channels\":[{\"name\":\"first.wav\",\"notes\":[{\"x\":1,\"y\":0,\"l\":0}]}]}");
                var pendingBmsonPackage = ChartPackage.FromChartEntries(
                [
                    PackageChartEntry.FromChart(
                        (ChartTestValues.ReadBmson(pendingBmsonPath)))
                ]);
                pendingBmsonPackage.path = pendingBmsonPath;
                pendingBmsonPackage.delete_parent = true;
                library.ChartPackagesPending = CreatePackageCollection([pendingPackage, pendingBmsonPackage]);

                library.SearchEstimatedInstallationDirectory(pendingBmsonPackage);

                PackageChartEntry pendingBmsonEntry = pendingBmsonPackage.ChartEntries.Single();
                Assert.IsNull(pendingBmsonEntry.GetBmsChartForTest());
                Assert.AreEqual(
                    Path.Combine(libraryRootPath, "[First Artist] First Title"),
                    pendingBmsonEntry.Chart.InstallDestination);
                Assert.AreNotEqual(firstDirectoryPath, pendingBmsonEntry.Chart.InstallDestination);
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public void AutoRenameChartFolders_ResolvesBatchDestinationCollisionsWithSuffix()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_AutoRenameCollision_" + Guid.NewGuid().ToString("N"));
            string libraryRootPath = Path.Combine(tempRootPath, "LibraryRoot");
            string firstDirectoryPath = Path.Combine(libraryRootPath, "FirstSource");
            string secondDirectoryPath = Path.Combine(libraryRootPath, "SecondSource");
            string firstChartPath = Path.Combine(firstDirectoryPath, "first.bms");
            string secondChartPath = Path.Combine(secondDirectoryPath, "second.bms");
            Directory.CreateDirectory(firstDirectoryPath);
            Directory.CreateDirectory(secondDirectoryPath);
            File.WriteAllText(firstChartPath, "#PLAYER 1");
            File.WriteAllText(secondChartPath, "#PLAYER 1");
            try
            {
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
                {
                    SearchTargets = [libraryRootPath]
                };
                ChartFile firstFile = ChartTestValues.Empty() with
                {
                    Path = firstChartPath
                };
                firstFile = firstFile with { Token = new OwnedChartToken() };
                firstFile = firstFile with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                firstFile = firstFile with { Title = "Same Title", RawTitle = "Same Title" };
                firstFile = firstFile with { Artist = "Same Artist", RawArtist = "Same Artist" };
                ChartFile secondFile = ChartTestValues.Empty() with
                {
                    Path = secondChartPath
                };
                secondFile = secondFile with { Token = new OwnedChartToken() };
                secondFile = secondFile with { Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" };
                secondFile = secondFile with { Title = "Same Title", RawTitle = "Same Title" };
                secondFile = secondFile with { Artist = "Same Artist", RawArtist = "Same Artist" };
                library.BmsCharts = [firstFile, secondFile];

                library.AutoRenameChartFoldersWithProgress([
                    (firstFile),
                    (secondFile)
                ], false, new RecordingFolderAutoRenameProgressWriter());

                string firstDestinationPath = Path.Combine(libraryRootPath, "[Same Artist] Same Title");
                string secondDestinationPath = Path.Combine(libraryRootPath, "[Same Artist] Same Title (2)");
                Assert.AreEqual(Path.Combine(firstDestinationPath, "first.bms"), CurrentOwnedChart(library, firstFile).Path);
                Assert.AreEqual(Path.Combine(secondDestinationPath, "second.bms"), CurrentOwnedChart(library, secondFile).Path);
                Assert.IsFalse(Directory.Exists(firstDirectoryPath));
                Assert.IsFalse(Directory.Exists(secondDirectoryPath));
                Assert.IsTrue(Directory.Exists(firstDestinationPath));
                Assert.IsTrue(Directory.Exists(secondDestinationPath));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public void AutoRenameChartFolders_DoesNotFailWhenLongChartFileNameExceedsLr2LegacyPathLimit()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_AutoRenameLongName_" + Guid.NewGuid().ToString("N"));
            string libraryRootPath = Path.Combine(tempRootPath, "S");
            string sourceDirectoryPath = Path.Combine(libraryRootPath, "O");
            Directory.CreateDirectory(sourceDirectoryPath);
            int fileNameBaseLength = 252 - sourceDirectoryPath.Length - Path.DirectorySeparatorChar.ToString().Length - ".bms".Length;
            if (fileNameBaseLength < 1)
            {
                Assert.Inconclusive("Temporary directory path is too long for this path length boundary test.");
            }
            string chartFileName = new string('x', fileNameBaseLength) + ".bms";
            string chartPath = Path.Combine(sourceDirectoryPath, chartFileName);
            File.WriteAllText(chartPath, "#PLAYER 1");
            try
            {
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
                {
                    SearchTargets = [libraryRootPath]
                };
                ChartFile file = ChartTestValues.Empty() with
                {
                    Path = chartPath
                };
                file = file with { Token = new OwnedChartToken() };
                file = file with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                file = file with { Title = "T", RawTitle = "T" };
                file = file with { Artist = "A", RawArtist = "A" };
                library.BmsCharts = [file];

                library.AutoRenameChartFoldersWithProgress([(file)], false, new RecordingFolderAutoRenameProgressWriter());

                string destinationDirectoryPath = Path.Combine(libraryRootPath, "[A] T");
                Assert.AreEqual(Path.Combine(destinationDirectoryPath, chartFileName), CurrentOwnedChart(library, file).Path);
                Assert.IsFalse(Directory.Exists(sourceDirectoryPath));
                Assert.IsTrue(Directory.Exists(destinationDirectoryPath));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public void AutoRenameChartFolders_Lr2FinalizationFailureReturnsDurableNonSuccess()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(
                Path.GetTempPath(),
                "BeMusicSeeker_AutoRenameDurableFinalizationFailure_" + Guid.NewGuid().ToString("N"));
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Source");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "[Artist] PackFinalizationFailure");
            string sourceChartPath = Path.Combine(sourceDirectoryPath, "chart.bms");
            string destinationChartPath = Path.Combine(destinationDirectoryPath, "chart.bms");
            string lr2RootPath = Path.Combine(tempRootPath, "LR2beta3");
            Directory.CreateDirectory(sourceDirectoryPath);
            File.WriteAllText(sourceChartPath, "#PLAYER 1\r\n#TITLE PackFinalizationFailure\r\n#ARTIST Artist\r\n");
            try
            {
                ChartFile file = (ChartTestValues.Empty() with { Path = sourceChartPath });
                file = file with { Token = new OwnedChartToken() };
                file = file with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                file = file with { Title = "PackFinalizationFailure", RawTitle = "PackFinalizationFailure" };
                file = file with { Artist = "Artist", RawArtist = "Artist" };
                LR2Config lr2Config = BmsPlaylistTestSupport.CreateLr2Config(lr2RootPath, tempRootPath);
                var library = new TestBmsLibrary(
                    songDbPath,
                    () => lr2Config,
                    null,
                    new TestFileMutationService(),
                    new RecordingDialogService(),
                    new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    () => new BmsLibraryOptionsSnapshot
                    {
                        OperationModeLR2DB = true,
                        LR2RootPath = lr2RootPath,
                        FolderNameFormat = "[%ARTIST%] %TITLE%"
                    })
                {
                    BmsCharts = [file]
                };
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                    songDb.Execute(
                        "CREATE TRIGGER fail_lr2_folder_insert BEFORE INSERT ON folder WHEN NEW.path LIKE '%PackFinalizationFailure%' "
                        + "BEGIN SELECT RAISE(ABORT, 'forced durable finalization failure'); END;");
                }

                int ownedCollectionPublicationCount = 0;
                int normalRefreshPublicationCount = 0;
                library.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(BMSLibrary.OwnedCollectionVersion))
                    {
                        ownedCollectionPublicationCount++;
                    }
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        normalRefreshPublicationCount++;
                    }
                };

                AutoRenameBatchResult result = library.AutoRenameChartFoldersWithProgress(
                    [(file)], false, new RecordingFolderAutoRenameProgressWriter());

                Assert.IsNotNull(result);
                Assert.IsTrue(result.HasDurableCommit);
                Assert.AreEqual(new LibraryMutationSessionApplyCounts
                {
                    CatalogApplyCount = 1,
                    PackageReferenceApplyCount = 1,
                    Lr2SyncCount = 1
                }, result.SessionReceipt.ApplyCounts);
                Assert.IsTrue(result.HasDurableFinalizationFailure);
                Assert.IsNotNull(result.PrimaryFailure);
                Assert.AreEqual(1, result.AppliedPlanCount);
                Assert.AreEqual(1, result.SessionReceipt.ConfirmedChangeCount);
                Assert.AreEqual(1, result.SessionReceipt.CatalogFolderPathChangeCount);
                Assert.AreSame(result.SessionReceipt.ApplyFailure, result.PrimaryFailure.SourceException);
                Assert.IsTrue(result.SessionReceipt.HasDurableFinalizationFailure);
                Assert.IsTrue(Directory.Exists(destinationDirectoryPath));
                Assert.IsFalse(Directory.Exists(sourceDirectoryPath));
                Assert.IsTrue(File.Exists(destinationChartPath));
                Assert.AreEqual(destinationChartPath, CurrentOwnedChart(library, file).Path);
                Assert.AreEqual(0, ownedCollectionPublicationCount);
                Assert.AreEqual(0, normalRefreshPublicationCount);
                using var verifySongDb = new LR2SongDBExtended(songDbPath);
                Assert.IsNotNull(verifySongDb.Find<LR2SongDB.song>(destinationChartPath));
                Assert.IsNull(verifySongDb.Find<LR2SongDB.song>(sourceChartPath));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ApplyAutoRenamePlans_UnexpectedMoveFailureCommitsConfirmedPrefixAndStopsSuffix(bool reportAtTerminal)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_AutoRenamePartialBatch_" + Guid.NewGuid().ToString("N"));
            string libraryRootPath = Path.Combine(tempRootPath, "LibraryRoot");
            // Auto-rename planning orders source folders by path length so ancestors are handled before descendants.
            // Keep these sibling names strictly increasing in length to make the intended prefix/failure/suffix order explicit.
            string firstDirectoryPath = Path.Combine(libraryRootPath, "A");
            string firstDestinationPath = Path.Combine(libraryRootPath, "[First Artist] First Renamed");
            string secondDirectoryPath = Path.Combine(libraryRootPath, "BB");
            string secondDestinationPath = Path.Combine(libraryRootPath, "[Second Artist] Second Renamed");
            string thirdDirectoryPath = Path.Combine(libraryRootPath, "CCC");
            string thirdDestinationPath = Path.Combine(libraryRootPath, "[Third Artist] Third Renamed");
            string firstChartPath = Path.Combine(firstDirectoryPath, "first.bms");
            string secondChartPath = Path.Combine(secondDirectoryPath, "second.bms");
            string thirdChartPath = Path.Combine(thirdDirectoryPath, "third.bms");
            Directory.CreateDirectory(firstDirectoryPath);
            Directory.CreateDirectory(secondDirectoryPath);
            Directory.CreateDirectory(thirdDirectoryPath);
            File.WriteAllText(firstChartPath, "#PLAYER 1");
            File.WriteAllText(secondChartPath, "#PLAYER 1");
            File.WriteAllText(thirdChartPath, "#PLAYER 1");
            try
            {
                var fileMutationService = new TestFileMutationService
                {
                    MoveDirectoryFailureSourcePath = secondDirectoryPath
                };
                var dialogs = new FileDbReportRecordingDialogs();
                var library = new TestBmsLibrary(songDbPath, null, null, fileMutationService, dialogs)
                {
                    SearchTargets = [libraryRootPath]
                };
                ChartFile firstFile = (ChartTestValues.Empty() with { Path = firstChartPath });
                firstFile = firstFile with { Token = new OwnedChartToken() };
                firstFile = firstFile with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                firstFile = firstFile with { Title = "First Renamed", RawTitle = "First Renamed" };
                firstFile = firstFile with { Artist = "First Artist", RawArtist = "First Artist" };
                ChartFile secondFile = (ChartTestValues.Empty() with { Path = secondChartPath });
                secondFile = secondFile with { Token = new OwnedChartToken() };
                secondFile = secondFile with { Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" };
                secondFile = secondFile with { Title = "Second Renamed", RawTitle = "Second Renamed" };
                secondFile = secondFile with { Artist = "Second Artist", RawArtist = "Second Artist" };
                ChartFile thirdFile = (ChartTestValues.Empty() with { Path = thirdChartPath });
                thirdFile = thirdFile with { Token = new OwnedChartToken() };
                thirdFile = thirdFile with { Md5 = "cccccccccccccccccccccccccccccccc" };
                thirdFile = thirdFile with { Title = "Third Renamed", RawTitle = "Third Renamed" };
                thirdFile = thirdFile with { Artist = "Third Artist", RawArtist = "Third Artist" };
                library.BmsCharts = [firstFile, secondFile, thirdFile];
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(firstFile), typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(secondFile), typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(thirdFile), typeof(LR2SongDB.song));
                }
                int refreshCount = 0;
                library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
                {
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        Interlocked.Increment(ref refreshCount);
                    }
                };
                int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;

                AutoRenameBatchResult result = library.AutoRenameChartFoldersWithProgress([
                    (firstFile),
                    (secondFile),
                    (thirdFile)
                ], false, new RecordingFolderAutoRenameProgressWriter(), reportAtTerminal: reportAtTerminal);

                Assert.AreEqual(reportAtTerminal ? 0 : 1, dialogs.ModelMessages);
                Assert.IsTrue(result.HasDurableCommit);
                Assert.AreEqual(1, result.AppliedPlanCount);
                Assert.AreEqual(1, result.SessionReceipt.ConfirmedChangeCount);
                Assert.AreEqual(1, result.SessionReceipt.CatalogFolderPathChangeCount);
                Assert.IsNotNull(result.SessionReceipt.PhysicalFailure);
                Assert.AreEqual(secondDirectoryPath, result.SessionReceipt.FailedTarget.SourcePath);
                Assert.AreEqual(secondDestinationPath, result.SessionReceipt.FailedTarget.DestinationPath);
                Assert.AreEqual(1, result.SessionReceipt.UnprocessedTargets.Count);
                Assert.AreEqual(thirdDirectoryPath, result.SessionReceipt.UnprocessedTargets[0].SourcePath);
                Assert.AreEqual(thirdDestinationPath, result.SessionReceipt.UnprocessedTargets[0].DestinationPath);
                NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

                Assert.AreEqual(1, Volatile.Read(ref refreshCount));
                Assert.IsTrue(batch.ChangedCharts.Count > 0);
                Assert.AreEqual(0, batch.DeletedTokens.Count);
                Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
                Assert.IsFalse(batch.ResetsPriorNotifications);
                Assert.AreEqual(Path.Combine(firstDestinationPath, "first.bms"), CurrentOwnedChart(library, firstFile).Path);
                Assert.AreEqual(secondChartPath, secondFile.Path);
                Assert.AreEqual(thirdChartPath, thirdFile.Path);
                Assert.IsFalse(Directory.Exists(firstDirectoryPath));
                Assert.IsTrue(Directory.Exists(firstDestinationPath));
                Assert.IsTrue(Directory.Exists(secondDirectoryPath));
                Assert.IsFalse(Directory.Exists(secondDestinationPath));
                Assert.IsTrue(Directory.Exists(thirdDirectoryPath));
                Assert.IsFalse(Directory.Exists(thirdDestinationPath));
                using var verifySongDb = new LR2SongDBExtended(songDbPath);
                Assert.IsNull(verifySongDb.Find<LR2SongDB.song>(firstChartPath));
                Assert.IsNotNull(verifySongDb.Find<LR2SongDB.song>(Path.Combine(firstDestinationPath, "first.bms")));
                Assert.IsNotNull(verifySongDb.Find<LR2SongDB.song>(secondChartPath));
                Assert.IsNotNull(verifySongDb.Find<LR2SongDB.song>(thirdChartPath));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public void AutoRenameChartFolders_PublicPublicationFailureIsBestEffortAfterLeaseRelease()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(
                Path.GetTempPath(),
                "BeMusicSeeker_AutoRenamePublicationFailure_" + Guid.NewGuid().ToString("N"));
            string libraryRootPath = Path.Combine(tempRootPath, "LibraryRoot");
            string firstSourceDirectoryPath = Path.Combine(libraryRootPath, "FirstSource");
            string secondSourceDirectoryPath = Path.Combine(libraryRootPath, "SecondSource");
            string firstChartPath = Path.Combine(firstSourceDirectoryPath, "first.bms");
            string secondChartPath = Path.Combine(secondSourceDirectoryPath, "second.bms");
            Directory.CreateDirectory(firstSourceDirectoryPath);
            Directory.CreateDirectory(secondSourceDirectoryPath);
            File.WriteAllText(firstChartPath, "#PLAYER 1");
            File.WriteAllText(secondChartPath, "#PLAYER 1");
            try
            {
                var library = new TestBmsLibrary(
                    songDbPath,
                    null,
                    null,
                    new TestFileMutationService(),
                    new RecordingDialogService())
                {
                    SearchTargets = [libraryRootPath]
                };
                ChartFile firstFile = (ChartTestValues.Empty() with { Path = firstChartPath });
                firstFile = firstFile with { Token = new OwnedChartToken() };
                firstFile = firstFile with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                firstFile = firstFile with { Title = "First Title", RawTitle = "First Title" };
                firstFile = firstFile with { Artist = "First Artist", RawArtist = "First Artist" };
                ChartFile secondFile = (ChartTestValues.Empty() with { Path = secondChartPath });
                secondFile = secondFile with { Token = new OwnedChartToken() };
                secondFile = secondFile with { Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" };
                secondFile = secondFile with { Title = "Second Title", RawTitle = "Second Title" };
                secondFile = secondFile with { Artist = "Second Artist", RawArtist = "Second Artist" };
                library.BmsCharts = [firstFile, secondFile];
                int publicationCount = 0;
                int postReleasePublicationCount = 0;
                int refreshCount = 0;
                library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
                {
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        refreshCount++;
                        return;
                    }
                    if (args.PropertyName != nameof(BMSLibrary.OwnedCollectionVersion))
                    {
                        return;
                    }
                    publicationCount++;
                    using LibraryFileMutationLease probe = library.TryBeginLibraryFileMutation(
                        "auto_rename_publication_probe");
                    if (probe != null)
                    {
                        postReleasePublicationCount++;
                    }
                    if (publicationCount == 1)
                    {
                        throw new InvalidOperationException("public publication failure");
                    }
                };

                AutoRenameBatchResult result = library.AutoRenameChartFoldersWithProgress([
                    (firstFile),
                    (secondFile)
                ], false, new RecordingFolderAutoRenameProgressWriter());

                Assert.IsNotNull(result);
                Assert.IsTrue(result.HasActionablePlan);
                Assert.AreEqual(2, result.AppliedPlanCount);
                Assert.IsTrue(result.HasDurableCommit);
                Assert.IsFalse(result.ManualRecoveryRequired);
                Assert.IsNull(result.PrimaryFailure);
                Assert.AreEqual(2, result.SessionReceipt.ConfirmedChangeCount);
                Assert.AreEqual(2, result.SessionReceipt.CatalogFolderPathChangeCount);
                Assert.AreEqual(2, result.SessionReceipt.FolderReferenceMoveCount);
                Assert.IsNull(result.SessionReceipt.PhysicalFailure);
                Assert.IsNull(result.SessionReceipt.ApplyFailure);
                Assert.IsNull(result.SessionReceipt.FinalizationFailure);
                Assert.AreEqual(1, publicationCount);
                Assert.AreEqual(1, postReleasePublicationCount);
                Assert.AreEqual(1, refreshCount);
                Assert.IsFalse(Directory.Exists(firstSourceDirectoryPath));
                Assert.IsFalse(Directory.Exists(secondSourceDirectoryPath));
                Assert.IsTrue(Directory.Exists(Path.Combine(libraryRootPath, "[First Artist] First Title")));
                Assert.IsTrue(Directory.Exists(Path.Combine(libraryRootPath, "[Second Artist] Second Title")));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public async Task MoveLibraryRootFolder_BmsChart_NotifiesBmsStorageRowsThroughRefreshNotification()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        await WithTemporarySongDbAsync(async delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_MoveRefresh_" + Guid.NewGuid().ToString("N"));
            string sourceRootPath = Path.Combine(tempRootPath, "SourceRoot");
            string destinationParentPath = Path.Combine(tempRootPath, "DestinationParent");
            string chartPath = Path.Combine(sourceRootPath, "chart.bms");
            Directory.CreateDirectory(sourceRootPath);
            Directory.CreateDirectory(destinationParentPath);
            File.WriteAllText(chartPath, "#PLAYER 1");
            try
            {
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                ChartFile file = ChartTestValues.Empty() with
                {
                    Path = chartPath
                };
                file = file with { Token = new OwnedChartToken() };
                file = file with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                int bmsFilesChangedCount = 0;
                int normalLibraryRefreshCount = 0;
                var bmsFilesPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                library.PropertyChanged += delegate (object? sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        Interlocked.Increment(ref bmsFilesChangedCount);
                        bmsFilesPublished.TrySetResult(true);
                    }
                    if (e.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        Interlocked.Increment(ref normalLibraryRefreshCount);
                    }
                };
                library.BmsCharts = [file];
                await bmsFilesPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Interlocked.Exchange(ref bmsFilesChangedCount, 0);
                Interlocked.Exchange(ref normalLibraryRefreshCount, 0);
                int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;

                library.MoveLibraryRootFolder([LibraryChartRef.FromChartFile((file))], destinationParentPath);
                NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

                Assert.AreEqual(1, Volatile.Read(ref bmsFilesChangedCount));
                Assert.IsTrue(Volatile.Read(ref normalLibraryRefreshCount) > 0);
                Assert.IsTrue((batch.ChangedCharts.Count > 0 || batch.DeletedTokens.Count > 0 || batch.HasEffect(LibraryChartRefreshEffects.SourceChanged)));
                Assert.IsTrue(batch.ChangedCharts.Any(chart => chart.Kind == ChartFileKind.Bms));
                Assert.IsFalse(batch.ChangedCharts.Any(chart => chart.Kind == ChartFileKind.Bmson));
                Assert.IsTrue(CurrentOwnedChart(library, file).Path.Contains(Path.Combine("DestinationParent", "SourceRoot", "chart.bms")));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public async Task RenameBmsonFolder_UpdatesFolderWithoutRaisingCollectionRefresh()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        await WithTemporarySongDbAsync(async delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_BmsonRenameRefresh_" + Guid.NewGuid().ToString("N"));
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Source");
            string chartPath = Path.Combine(sourceDirectoryPath, "chart.bmson");
            Directory.CreateDirectory(sourceDirectoryPath);
            File.WriteAllText(chartPath, "{}");
            try
            {
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
                {
                    Path = chartPath,
                    Folder = sourceDirectoryPath,
                    Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    RawTitle = "Chart"
                };
                song = song with { Token = new OwnedChartToken() };
                int bmsFilesChangedCount = 0;
                int bmsonSongsChangedCount = 0;
                var bmsonSongsPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                library.PropertyChanged += delegate (object? sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        Interlocked.Increment(ref bmsFilesChangedCount);
                    }
                    if (e.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        Interlocked.Increment(ref bmsonSongsChangedCount);
                        bmsonSongsPublished.TrySetResult(true);
                    }
                };
                bmsonSongsPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                library.BmsonCharts = [song];
                await bmsonSongsPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Interlocked.Exchange(ref bmsFilesChangedCount, 0);
                Interlocked.Exchange(ref bmsonSongsChangedCount, 0);

                library.RenameChartFolder(sourceDirectoryPath, "Renamed");
                var row = LibraryChartRow.FromChartFile(CurrentOwnedChart(library, song));

                Assert.AreEqual(1, Volatile.Read(ref bmsFilesChangedCount));
                Assert.AreEqual(1, Volatile.Read(ref bmsonSongsChangedCount));
                Assert.AreEqual("Renamed", row.Folder);
                Assert.IsTrue(CurrentOwnedChart(library, song).Path.Contains(Path.Combine("Renamed", "chart.bmson")));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MoveLibraryRootFolder_DatabaseApplyFailureKeepsConfirmedPhysicalMoves(bool reportAtTerminal)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_FolderSessionDbFailure_" + Guid.NewGuid().ToString("N"));
            string sourceRootPath = Path.Combine(tempRootPath, "SourceRoot");
            string firstSourceDirectoryPath = Path.Combine(sourceRootPath, "A");
            string secondSourceDirectoryPath = Path.Combine(sourceRootPath, "BB");
            string destinationRootPath = Path.Combine(tempRootPath, "DestinationRoot");
            string firstChartPath = Path.Combine(firstSourceDirectoryPath, "first.bms");
            string secondChartPath = Path.Combine(secondSourceDirectoryPath, "second.bms");
            string firstDestinationChartPath = Path.Combine(destinationRootPath, "A", "first.bms");
            string secondDestinationChartPath = Path.Combine(destinationRootPath, "BB", "second.bms");
            Directory.CreateDirectory(firstSourceDirectoryPath);
            Directory.CreateDirectory(secondSourceDirectoryPath);
            Directory.CreateDirectory(destinationRootPath);
            File.WriteAllText(firstChartPath, "#PLAYER 1\r\n#TITLE First");
            File.WriteAllText(secondChartPath, "#PLAYER 1\r\n#TITLE Second");
            try
            {
                var dialogs = new RecordingDialogService();
                var library = new TestBmsLibrary(
                    songDbPath,
                    null,
                    null,
                    new TestFileMutationService(),
                    dialogs);
                ChartFile firstFile = (ChartTestValues.Empty() with { Path = firstChartPath });
                firstFile = firstFile with { Token = new OwnedChartToken() };
                firstFile = firstFile with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                ChartFile secondFile = (ChartTestValues.Empty() with { Path = secondChartPath });
                secondFile = secondFile with { Token = new OwnedChartToken() };
                secondFile = secondFile with { Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" };
                library.BmsCharts = [firstFile, secondFile];
                int baselineOwnedCollectionVersion = library.OwnedCollectionVersion;
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(firstFile), typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(secondFile), typeof(LR2SongDB.song));
                    string escapedPath = firstDestinationChartPath.Replace("'", "''");
                    songDb.Execute(
                        "CREATE TRIGGER fail_folder_session_move BEFORE INSERT ON song WHEN NEW.path = '"
                        + escapedPath
                        + "' BEGIN SELECT RAISE(ABORT, 'forced folder session move failure'); END;");
                }

                LibraryMutationSessionReceipt receipt = library.MoveLibraryRootFolderWithReceipt(
                    [
                        LibraryChartRef.FromChartFile((firstFile)),
                        LibraryChartRef.FromChartFile((secondFile))
                    ],
                    destinationRootPath,
                    reportAtTerminal: reportAtTerminal);

                Assert.AreEqual(2, receipt.ConfirmedChangeCount);
                Assert.IsFalse(receipt.DurableCommit);
                Assert.AreEqual(new LibraryMutationSessionApplyCounts
                {
                    CatalogApplyCount = 1
                }, receipt.ApplyCounts);
                Assert.IsNull(receipt.PhysicalFailure);
                Assert.IsNotNull(receipt.ApplyFailure);
                Assert.AreEqual(0, receipt.UnprocessedTargets.Count);
                Assert.AreEqual(reportAtTerminal ? 0 : 1, dialogs.CallCount);
                Assert.AreEqual(baselineOwnedCollectionVersion, library.OwnedCollectionVersion);
                Assert.IsFalse(Directory.Exists(firstSourceDirectoryPath));
                Assert.IsFalse(Directory.Exists(secondSourceDirectoryPath));
                Assert.IsTrue(File.Exists(firstDestinationChartPath));
                Assert.IsTrue(File.Exists(secondDestinationChartPath));
                Assert.AreEqual(firstChartPath, CurrentOwnedChart(library, firstFile).Path);
                Assert.AreEqual(secondChartPath, CurrentOwnedChart(library, secondFile).Path);
                using var verifySongDb = new LR2SongDBExtended(songDbPath);
                Assert.IsNotNull(verifySongDb.Find<LR2SongDB.song>(firstChartPath));
                Assert.IsNotNull(verifySongDb.Find<LR2SongDB.song>(secondChartPath));
                Assert.IsNull(verifySongDb.Find<LR2SongDB.song>(firstDestinationChartPath));
                Assert.IsNull(verifySongDb.Find<LR2SongDB.song>(secondDestinationChartPath));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MoveLibraryRootFolder_PhysicalFailureCommitsConfirmedPrefixAndStopsSuffix(bool reportAtTerminal)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_FolderSessionPartial_" + Guid.NewGuid().ToString("N"));
            string sourceRootPath = Path.Combine(tempRootPath, "SourceRoot");
            string firstSourceDirectoryPath = Path.Combine(sourceRootPath, "A");
            string secondSourceDirectoryPath = Path.Combine(sourceRootPath, "BB");
            string thirdSourceDirectoryPath = Path.Combine(sourceRootPath, "CCC");
            string destinationRootPath = Path.Combine(tempRootPath, "DestinationRoot");
            string firstChartPath = Path.Combine(firstSourceDirectoryPath, "first.bms");
            string secondChartPath = Path.Combine(secondSourceDirectoryPath, "second.bms");
            string thirdChartPath = Path.Combine(thirdSourceDirectoryPath, "third.bms");
            string firstDestinationDirectoryPath = Path.Combine(destinationRootPath, "A");
            string secondDestinationDirectoryPath = Path.Combine(destinationRootPath, "BB");
            string thirdDestinationDirectoryPath = Path.Combine(destinationRootPath, "CCC");
            Directory.CreateDirectory(firstSourceDirectoryPath);
            Directory.CreateDirectory(secondSourceDirectoryPath);
            Directory.CreateDirectory(thirdSourceDirectoryPath);
            Directory.CreateDirectory(destinationRootPath);
            File.WriteAllText(firstChartPath, "#PLAYER 1");
            File.WriteAllText(secondChartPath, "#PLAYER 1");
            File.WriteAllText(thirdChartPath, "#PLAYER 1");
            try
            {
                var fileMutationService = new TestFileMutationService
                {
                    MoveDirectoryFailureSourcePath = secondSourceDirectoryPath
                };
                var dialogs = new RecordingDialogService();
                var library = new TestBmsLibrary(songDbPath, null, null, fileMutationService, dialogs);
                ChartFile firstFile = (ChartTestValues.Empty() with { Path = firstChartPath });
                firstFile = firstFile with { Token = new OwnedChartToken() };
                firstFile = firstFile with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                ChartFile secondFile = (ChartTestValues.Empty() with { Path = secondChartPath });
                secondFile = secondFile with { Token = new OwnedChartToken() };
                secondFile = secondFile with { Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" };
                ChartFile thirdFile = (ChartTestValues.Empty() with { Path = thirdChartPath });
                thirdFile = thirdFile with { Token = new OwnedChartToken() };
                thirdFile = thirdFile with { Md5 = "cccccccccccccccccccccccccccccccc" };
                library.BmsCharts = [firstFile, secondFile, thirdFile];
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(firstFile), typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(secondFile), typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(thirdFile), typeof(LR2SongDB.song));
                }
                int refreshCount = 0;
                library.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        refreshCount++;
                    }
                };

                LibraryMutationSessionReceipt receipt = library.MoveLibraryRootFolderWithReceipt(
                    [
                        LibraryChartRef.FromChartFile((firstFile)),
                        LibraryChartRef.FromChartFile((secondFile)),
                        LibraryChartRef.FromChartFile((thirdFile))
                    ],
                    destinationRootPath,
                    reportAtTerminal: reportAtTerminal);

                Assert.IsTrue(receipt.DurableCommit);
                Assert.AreEqual(1, receipt.ConfirmedChangeCount);
                Assert.AreEqual(1, receipt.CatalogChartPathChangeCount);
                Assert.AreEqual(1, receipt.FolderReferenceMoveCount);
                Assert.IsNotNull(receipt.PhysicalFailure);
                Assert.AreEqual(secondSourceDirectoryPath, receipt.FailedTarget.SourcePath);
                Assert.AreEqual(secondDestinationDirectoryPath, receipt.FailedTarget.DestinationPath);
                Assert.AreEqual(1, receipt.UnprocessedTargets.Count);
                Assert.AreEqual(thirdSourceDirectoryPath, receipt.UnprocessedTargets[0].SourcePath);
                Assert.AreEqual(thirdDestinationDirectoryPath, receipt.UnprocessedTargets[0].DestinationPath);
                Assert.AreEqual(reportAtTerminal ? 0 : 1, dialogs.CallCount);
                Assert.AreEqual(1, refreshCount);
                Assert.AreEqual(Path.Combine(firstDestinationDirectoryPath, "first.bms"), CurrentOwnedChart(library, firstFile).Path);
                Assert.AreEqual(secondChartPath, CurrentOwnedChart(library, secondFile).Path);
                Assert.AreEqual(thirdChartPath, CurrentOwnedChart(library, thirdFile).Path);
                Assert.IsFalse(Directory.Exists(firstSourceDirectoryPath));
                Assert.IsTrue(Directory.Exists(firstDestinationDirectoryPath));
                Assert.IsTrue(Directory.Exists(secondSourceDirectoryPath));
                Assert.IsFalse(Directory.Exists(secondDestinationDirectoryPath));
                Assert.IsTrue(Directory.Exists(thirdSourceDirectoryPath));
                Assert.IsFalse(Directory.Exists(thirdDestinationDirectoryPath));
                using var verifySongDb = new LR2SongDBExtended(songDbPath);
                Assert.IsNull(verifySongDb.Find<LR2SongDB.song>(firstChartPath));
                Assert.IsNotNull(verifySongDb.Find<LR2SongDB.song>(Path.Combine(firstDestinationDirectoryPath, "first.bms")));
                Assert.IsNotNull(verifySongDb.Find<LR2SongDB.song>(secondChartPath));
                Assert.IsNotNull(verifySongDb.Find<LR2SongDB.song>(thirdChartPath));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public async Task MoveLibraryRootFolder_BmsonChart_NotifiesBmsonStorageRowsThroughRefreshNotification()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        await WithTemporarySongDbAsync(async delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_BmsonMoveRefresh_" + Guid.NewGuid().ToString("N"));
            string sourceRootPath = Path.Combine(tempRootPath, "SourceRoot");
            string destinationParentPath = Path.Combine(tempRootPath, "DestinationParent");
            string chartPath = Path.Combine(sourceRootPath, "chart.bmson");
            Directory.CreateDirectory(sourceRootPath);
            Directory.CreateDirectory(destinationParentPath);
            File.WriteAllText(chartPath, "{}");
            try
            {
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
                {
                    Path = chartPath,
                    Folder = sourceRootPath,
                    Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    RawTitle = "Chart"
                };
                song = song with { Token = new OwnedChartToken() };
                int bmsFilesChangedCount = 0;
                int bmsonSongsChangedCount = 0;
                int normalLibraryRefreshCount = 0;
                var bmsonSongsPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                library.PropertyChanged += delegate (object? sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        Interlocked.Increment(ref bmsFilesChangedCount);
                    }
                    if (e.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        Interlocked.Increment(ref bmsonSongsChangedCount);
                        bmsonSongsPublished.TrySetResult(true);
                    }
                    if (e.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        Interlocked.Increment(ref normalLibraryRefreshCount);
                    }
                };
                bmsonSongsPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                library.BmsonCharts = [song];
                await bmsonSongsPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Interlocked.Exchange(ref bmsFilesChangedCount, 0);
                Interlocked.Exchange(ref bmsonSongsChangedCount, 0);
                Interlocked.Exchange(ref normalLibraryRefreshCount, 0);
                int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;

                library.MoveLibraryRootFolder([LibraryChartRef.FromChartFile((song))], destinationParentPath);
                NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

                Assert.AreEqual(1, Volatile.Read(ref bmsFilesChangedCount));
                Assert.AreEqual(1, Volatile.Read(ref bmsonSongsChangedCount));
                Assert.IsTrue(Volatile.Read(ref normalLibraryRefreshCount) > 0);
                Assert.IsTrue((batch.ChangedCharts.Count > 0 || batch.DeletedTokens.Count > 0 || batch.HasEffect(LibraryChartRefreshEffects.SourceChanged)));
                Assert.IsFalse(batch.ChangedCharts.Any(chart => chart.Kind == ChartFileKind.Bms));
                Assert.IsTrue(batch.ChangedCharts.Any(chart => chart.Kind == ChartFileKind.Bmson));
                Assert.IsTrue(CurrentOwnedChart(library, song).Path.Contains(Path.Combine("DestinationParent", "SourceRoot", "chart.bmson")));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public async Task MoveLibraryRootFolder_MixedFoldersPublishesOneOperationNotification()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        await WithTemporarySongDbAsync(async delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_MixedMoveRefresh_" + Guid.NewGuid().ToString("N"));
            string sourceRootPath = Path.Combine(tempRootPath, "SourceRoot");
            string bmsDirectoryPath = Path.Combine(sourceRootPath, "A");
            string bmsonDirectoryPath = Path.Combine(sourceRootPath, "BB");
            string destinationParentPath = Path.Combine(tempRootPath, "DestinationParent");
            string bmsPath = Path.Combine(bmsDirectoryPath, "chart.bms");
            string bmsonPath = Path.Combine(bmsonDirectoryPath, "chart.bmson");
            Directory.CreateDirectory(bmsDirectoryPath);
            Directory.CreateDirectory(bmsonDirectoryPath);
            Directory.CreateDirectory(destinationParentPath);
            File.WriteAllText(bmsPath, "#PLAYER 1");
            File.WriteAllText(bmsonPath, "{}");
            try
            {
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                ChartFile file = (ChartTestValues.Empty() with { Path = bmsPath });
                file = file with { Token = new OwnedChartToken() };
                file = file with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
                {
                    Path = bmsonPath,
                    Folder = bmsonDirectoryPath,
                    Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    RawTitle = "Chart"
                };
                song = song with { Token = new OwnedChartToken() };
                int ownedCollectionPublicationCount = 0;
                int normalLibraryRefreshCount = 0;
                var bmsFilesPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var bmsonSongsPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                library.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        bmsFilesPublished.TrySetResult(true);
                    }
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        bmsonSongsPublished.TrySetResult(true);
                    }
                    if (args.PropertyName == nameof(BMSLibrary.OwnedCollectionVersion))
                    {
                        Interlocked.Increment(ref ownedCollectionPublicationCount);
                    }
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        Interlocked.Increment(ref normalLibraryRefreshCount);
                    }
                };
                library.BmsCharts = [file];
                await bmsFilesPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                bmsonSongsPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                library.BmsonCharts = [song];
                await bmsonSongsPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Interlocked.Exchange(ref ownedCollectionPublicationCount, 0);
                Interlocked.Exchange(ref normalLibraryRefreshCount, 0);
                int baselineOwnedCollectionVersion = library.OwnedCollectionVersion;
                int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;

                LibraryMutationSessionReceipt receipt = library.MoveLibraryRootFolderWithReceipt(
                    [
                        LibraryChartRef.FromChartFile((file)),
                        LibraryChartRef.FromChartFile((song))
                    ],
                    destinationParentPath);
                NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

                Assert.IsTrue(receipt.DurableCommit, receipt.PrimaryFailure?.ToString());
                Assert.AreEqual(2, receipt.ConfirmedChangeCount);
                Assert.AreEqual(new LibraryMutationSessionApplyCounts
                {
                    CatalogApplyCount = 1,
                    PackageReferenceApplyCount = 1,
                    ReverseLookupApplyCount = 1,
                    Lr2SyncCount = 1,
                    RequiredPublicationCount = 1
                }, receipt.ApplyCounts);
                Assert.AreEqual(2, receipt.CatalogChartPathChangeCount);
                Assert.AreEqual(2, receipt.FolderReferenceMoveCount);
                Assert.AreEqual(baselineOwnedCollectionVersion + 1, library.OwnedCollectionVersion);
                Assert.AreEqual(1, Volatile.Read(ref ownedCollectionPublicationCount));
                Assert.AreEqual(1, Volatile.Read(ref normalLibraryRefreshCount));
                Assert.IsTrue((batch.ChangedCharts.Count > 0 || batch.DeletedTokens.Count > 0 || batch.HasEffect(LibraryChartRefreshEffects.SourceChanged)));
                Assert.IsTrue(batch.ChangedCharts.Any(chart => chart.Kind == ChartFileKind.Bms));
                Assert.IsTrue(batch.ChangedCharts.Any(chart => chart.Kind == ChartFileKind.Bmson));
                Assert.AreEqual(Path.Combine(destinationParentPath, "A", "chart.bms"), CurrentOwnedChart(library, file).Path);
                Assert.AreEqual(Path.Combine(destinationParentPath, "BB", "chart.bmson"), CurrentOwnedChart(library, song).Path);
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public void FixInstallationDirectoryCharts_BmsonChartUpdatesSongAndPersistedRow()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_BmsonRepair_" + Guid.NewGuid().ToString("N"));
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Broken");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed");
            string sourceChartPath = Path.Combine(sourceDirectoryPath, "chart.bmson");
            string destinationChartPath = Path.Combine(destinationDirectoryPath, "chart.bmson");
            Directory.CreateDirectory(sourceDirectoryPath);
            Directory.CreateDirectory(destinationDirectoryPath);
            File.WriteAllText(sourceChartPath, "{}");
            try
            {
                ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
                {
                    Path = sourceChartPath,
                    Folder = sourceDirectoryPath,
                    RawTitle = "Repair Bmson",
                    Md5 = "0123456789abcdef0123456789abcdef",
                    Sha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                };
                song = song with { Token = new OwnedChartToken() };
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(song), typeof(LR2SongDBExtended.bmson_song));
                }
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                library.BmsonCharts = [song];
                ChartFile repairTarget = ChartFileProjection.WithPackageState(
                    (song),
                    destinationDirectoryPath,
                    string.Empty,
                    string.Empty,
                    []);

                library.FixInstallationDirectoryCharts([repairTarget]);

                Assert.AreEqual(destinationChartPath, CurrentOwnedChart(library, song).Path);
                Assert.AreEqual(Path.GetFileName(destinationDirectoryPath), CurrentOwnedChart(library, song).Folder);
                Assert.IsFalse(File.Exists(sourceChartPath));
                Assert.IsTrue(File.Exists(destinationChartPath));
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    Assert.AreEqual(0, songDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == sourceChartPath));
                    LR2SongDBExtended.bmson_song persistedSong = songDb.Table<LR2SongDBExtended.bmson_song>().Single(row => row.path == destinationChartPath);
                    Assert.AreEqual(destinationDirectoryPath, persistedSong.folder);
                }
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FixInstallationDirectoryCharts_CatalogApplyFailureKeepsPhysicalMoveAndReportsSession(bool bmson)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RepairDbFailure_" + Guid.NewGuid().ToString("N"));
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Broken");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed");
            string sourceChartPath = Path.Combine(sourceDirectoryPath, bmson ? "chart.bmson" : "chart.bms");
            string destinationChartPath = Path.Combine(destinationDirectoryPath, Path.GetFileName(sourceChartPath));
            Directory.CreateDirectory(sourceDirectoryPath);
            Directory.CreateDirectory(destinationDirectoryPath);
            File.WriteAllText(sourceChartPath, bmson ? "{}" : "#PLAYER 1\r\n#TITLE Repair DB failure\r\n");
            try
            {
                ChartFile? bmsFile = null;
                ChartFile? bmsonSong = null;
                ChartFile chart;
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    if (bmson)
                    {
                        bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
                        {
                            Path = sourceChartPath,
                            Folder = sourceDirectoryPath,
                            RawTitle = "Repair DB failure",
                            Md5 = "abcdefabcdefabcdefabcdefabcdefab",
                            Sha256 = "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd"
                        };
                        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(bmsonSong), typeof(LR2SongDBExtended.bmson_song));
                        chart = (bmsonSong);
                    }
                    else
                    {
                        bmsFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(sourceChartPath));
                        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(bmsFile), typeof(LR2SongDB.song));
                        chart = (bmsFile);
                    }

                    string escapedDestinationPath = destinationChartPath.Replace("'", "''");
                    string tableName = bmson ? "bmson_song" : "song";
                    songDb.Execute(
                        "CREATE TRIGGER repair_path_insert_failure BEFORE INSERT ON " + tableName
                        + " WHEN NEW.path = '" + escapedDestinationPath
                        + "' BEGIN SELECT RAISE(ABORT, 'repair-path-write-fault'); END;");
                    songDb.Execute(
                        "CREATE TRIGGER repair_path_update_failure BEFORE UPDATE ON " + tableName
                        + " WHEN NEW.path = '" + escapedDestinationPath
                        + "' BEGIN SELECT RAISE(ABORT, 'repair-path-write-fault'); END;");
                }

                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
                {
                    BmsCharts = bmsFile == null ? [] : [bmsFile],
                    BmsonCharts = bmsonSong == null ? [] : [bmsonSong]
                };
                ChartFile repairTarget = ChartFileProjection.WithPackageState(
                    chart,
                    destinationDirectoryPath,
                    string.Empty,
                    string.Empty,
                    []);

                LibraryFixInstallationResult result = library.FixInstallationDirectoryCharts([repairTarget]);

                Assert.IsNotNull(result);
                Assert.IsNotNull(result.Failure);
                Assert.IsNotNull(result.SessionReceipt);
                Assert.IsFalse(result.SessionReceipt.DurableCommit);
                Assert.AreEqual(1, result.SessionReceipt.ConfirmedChangeCount);
                Assert.AreEqual(1, result.SessionReceipt.CatalogChartPathChangeCount);
                Assert.IsNotNull(result.SessionReceipt.ApplyFailure);
                Assert.IsFalse(File.Exists(sourceChartPath));
                Assert.IsTrue(File.Exists(destinationChartPath));
                Assert.AreEqual(sourceChartPath, bmsonSong?.Path ?? bmsFile!.Path);
                using var verifySongDb = new LR2SongDBExtended(songDbPath);
                if (bmson)
                {
                    Assert.AreEqual(1, verifySongDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == sourceChartPath));
                    Assert.AreEqual(0, verifySongDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == destinationChartPath));
                }
                else
                {
                    Assert.AreEqual(1, verifySongDb.Table<LR2SongDB.song>().Count(row => row.path == sourceChartPath));
                    Assert.AreEqual(0, verifySongDb.Table<LR2SongDB.song>().Count(row => row.path == destinationChartPath));
                }
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public void FixInstallationDirectoryCharts_MultipleRepairsShareOneCatalogTransaction()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RepairBatchDbFailure_" + Guid.NewGuid().ToString("N"));
            string firstSourceDirectory = Path.Combine(tempRootPath, "FirstBroken");
            string secondSourceDirectory = Path.Combine(tempRootPath, "SecondBroken");
            string destinationDirectory = Path.Combine(tempRootPath, "Installed");
            string firstSourcePath = Path.Combine(firstSourceDirectory, "first.bmson");
            string secondSourcePath = Path.Combine(secondSourceDirectory, "second.bmson");
            string firstDestinationPath = Path.Combine(destinationDirectory, "first.bmson");
            string secondDestinationPath = Path.Combine(destinationDirectory, "second.bmson");
            Directory.CreateDirectory(firstSourceDirectory);
            Directory.CreateDirectory(secondSourceDirectory);
            Directory.CreateDirectory(destinationDirectory);
            File.WriteAllText(firstSourcePath, "{}");
            File.WriteAllText(secondSourcePath, "{}");
            try
            {
                ChartFile firstSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
                {
                    Path = firstSourcePath,
                    Folder = firstSourceDirectory,
                    RawTitle = "First repair",
                    Md5 = "11111111111111111111111111111111",
                    Sha256 = "1111111111111111111111111111111111111111111111111111111111111111"
                };
                firstSong = firstSong with { Token = new OwnedChartToken() };
                ChartFile secondSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
                {
                    Path = secondSourcePath,
                    Folder = secondSourceDirectory,
                    RawTitle = "Second repair",
                    Md5 = "22222222222222222222222222222222",
                    Sha256 = "2222222222222222222222222222222222222222222222222222222222222222"
                };
                secondSong = secondSong with { Token = new OwnedChartToken() };
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(firstSong), typeof(LR2SongDBExtended.bmson_song));
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(secondSong), typeof(LR2SongDBExtended.bmson_song));
                    string escapedDestinationPath = secondDestinationPath.Replace("'", "''");
                    songDb.Execute(
                        "CREATE TRIGGER repair_batch_insert_failure BEFORE INSERT ON bmson_song"
                        + " WHEN NEW.path = '" + escapedDestinationPath
                        + "' BEGIN SELECT RAISE(ABORT, 'repair-batch-write-fault'); END;");
                    songDb.Execute(
                        "CREATE TRIGGER repair_batch_update_failure BEFORE UPDATE ON bmson_song"
                        + " WHEN NEW.path = '" + escapedDestinationPath
                        + "' BEGIN SELECT RAISE(ABORT, 'repair-batch-write-fault'); END;");
                }

                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
                {
                    BmsonCharts = [firstSong, secondSong]
                };
                ChartFile firstTarget = ChartFileProjection.WithPackageState(
                    (firstSong), destinationDirectory, string.Empty, string.Empty, []);
                ChartFile secondTarget = ChartFileProjection.WithPackageState(
                    (secondSong), destinationDirectory, string.Empty, string.Empty, []);

                LibraryFixInstallationResult result = library.FixInstallationDirectoryCharts(
                    [firstTarget, secondTarget],
                    approvedDuplicateRemovalChartPaths: []);

                Assert.IsNotNull(result.SessionReceipt);
                Assert.IsNotNull(result.Failure);
                Assert.IsFalse(result.SessionReceipt.DurableCommit);
                Assert.AreEqual(new LibraryMutationSessionApplyCounts
                {
                    CatalogApplyCount = 1
                }, result.SessionReceipt.ApplyCounts);
                Assert.AreEqual(2, result.MovedCount);
                Assert.AreEqual(2, result.SessionReceipt.ConfirmedChangeCount);
                Assert.AreEqual(2, result.SessionReceipt.CatalogChartPathChangeCount);
                Assert.AreEqual(2, result.SessionReceipt.PackageInstallDestinationChangeCount);
                Assert.IsNotNull(result.SessionReceipt.ApplyFailure);
                Assert.IsFalse(File.Exists(firstSourcePath));
                Assert.IsFalse(File.Exists(secondSourcePath));
                Assert.IsTrue(File.Exists(firstDestinationPath));
                Assert.IsTrue(File.Exists(secondDestinationPath));
                Assert.AreEqual(firstSourcePath, CurrentOwnedChart(library, firstSong).Path);
                Assert.AreEqual(secondSourcePath, CurrentOwnedChart(library, secondSong).Path);
                using var verifySongDb = new LR2SongDBExtended(songDbPath);
                Assert.AreEqual(1, verifySongDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == firstSourcePath));
                Assert.AreEqual(1, verifySongDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == secondSourcePath));
                Assert.AreEqual(0, verifySongDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == firstDestinationPath));
                Assert.AreEqual(0, verifySongDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == secondDestinationPath));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FixInstallationDirectoryCharts_UsesCollisionPathForOwnerAndDb(bool bmson)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RepairCollision_" + Guid.NewGuid().ToString("N"));
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Broken");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed");
            string sourceChartPath = Path.Combine(sourceDirectoryPath, bmson ? "chart.bmson" : "chart.bms");
            string collisionPath = Path.Combine(destinationDirectoryPath, Path.GetFileName(sourceChartPath));
            Directory.CreateDirectory(sourceDirectoryPath);
            Directory.CreateDirectory(destinationDirectoryPath);
            File.WriteAllText(sourceChartPath, bmson ? "{}" : "#PLAYER 1\r\n#TITLE Repair collision\r\n");
            File.WriteAllText(collisionPath, bmson ? "{\"existing\":true}" : "#PLAYER 1\r\n#TITLE Existing collision\r\n");
            byte[] collisionBytes = File.ReadAllBytes(collisionPath);
            try
            {
                ChartFile? bmsFile = null;
                ChartFile? bmsonSong = null;
                ChartFile chart;
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    if (bmson)
                    {
                        bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
                        {
                            Path = sourceChartPath,
                            Folder = sourceDirectoryPath,
                            RawTitle = "Repair collision",
                            Md5 = "abcdefabcdefabcdefabcdefabcdefab",
                            Sha256 = "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd"
                        };
                        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(bmsonSong), typeof(LR2SongDBExtended.bmson_song));
                        chart = (bmsonSong);
                    }
                    else
                    {
                        bmsFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(sourceChartPath));
                        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(bmsFile), typeof(LR2SongDB.song));
                        chart = (bmsFile);
                    }
                }

                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
                {
                    BmsCharts = bmsFile == null ? [] : [bmsFile],
                    BmsonCharts = bmsonSong == null ? [] : [bmsonSong]
                };
                chart = bmson ? library.BmsonCharts.Single() : library.BmsCharts.Single();
                ChartFile repairTarget = ChartFileProjection.WithPackageState(
                    chart,
                    destinationDirectoryPath,
                    string.Empty,
                    string.Empty,
                    []);

                LibraryFixInstallationResult result = library.FixInstallationDirectoryCharts([repairTarget]);

                Assert.IsNotNull(result);
                Assert.IsNull(result.Failure);
                Assert.IsNotNull(result.SessionReceipt);
                Assert.IsTrue(result.SessionReceipt.DurableCommit);
                Assert.AreEqual(1, result.SessionReceipt.CatalogChartPathChangeCount);
                string actualDestinationPath = result.SessionReceipt.ConfirmedTargets.Single().DestinationPath;
                Assert.AreNotEqual(collisionPath, actualDestinationPath, ignoreCase: true);
                Assert.IsTrue(File.Exists(actualDestinationPath));
                Assert.IsTrue(File.Exists(sourceChartPath) == false);
                Assert.IsTrue(collisionBytes.SequenceEqual(File.ReadAllBytes(collisionPath)));
                Assert.AreEqual(actualDestinationPath, CurrentOwnedChart(library, chart).Path);
                using var verifySongDb = new LR2SongDBExtended(songDbPath);
                if (bmson)
                {
                    Assert.AreEqual(0, verifySongDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == sourceChartPath));
                    Assert.AreEqual(1, verifySongDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == actualDestinationPath));
                }
                else
                {
                    Assert.AreEqual(0, verifySongDb.Table<LR2SongDB.song>().Count(row => row.path == sourceChartPath));
                    Assert.AreEqual(1, verifySongDb.Table<LR2SongDB.song>().Count(row => row.path == actualDestinationPath));
                }
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// 修正後の場所で不足リソースの健全性を再計算・保存し、既存の予約を解放してから公開します。
    /// パス移動が成功しても保守DBの失敗はエラーとして残します。
    /// </summary>
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void FixInstallationDirectoryCharts_RechecksResourcesUnderExistingReservation(bool bmson, bool failMaintenance)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string source = Path.Combine(root, "Broken");
            string destination = Path.Combine(root, "Installed");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            string sourcePath = Path.Combine(source, bmson ? "chart.bmson" : "chart.bms");
            string destinationPath = Path.Combine(destination, Path.GetFileName(sourcePath));
            File.WriteAllText(sourcePath, bmson
                ? "{\"version\":\"1.0.0\",\"info\":{\"title\":\"Repair\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\"},"
                    + "\"sound_channels\":[{\"name\":\"sound.wav\",\"notes\":[{\"x\":1,\"y\":0,\"l\":0}]}]}"
                : "#PLAYER 1\r\n#TITLE Repair\r\n#WAV01 sound.wav\r\n#00111:01\r\n");
            File.WriteAllText(Path.Combine(destination, "sound.wav"), "present only at the correct destination");
            ChartFile? bmsFile = bmson ? null : BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(sourcePath));
            ChartFile? bmsonSong = bmson ? ChartTestValues.ReadBmson(sourcePath) : null;
            ChartFile chart = (bmson ? (bmsonSong!) : (bmsFile!)) with { Token = new OwnedChartToken() };
            ResourceHealthMaintenanceSnapshot initialInfo = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(chart);
            Assert.AreEqual(1, initialInfo.WavFilesDefined);
            Assert.AreEqual(0, initialInfo.WavFilesExisting);
            chart = ChartFileProjection.WithMaintenance(chart, initialInfo);
            if (bmsonSong is not null)
            {
                bmsonSong = chart;
            }
            else if (bmsFile is not null)
            {
                bmsFile = chart;
            }

            chart = bmson ? (bmsonSong!) : (bmsFile!);
            Assert.IsTrue(new BmsLibraryMaintenanceService().BuildResourceHealthWarnings(chart)
                .Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                BmsLibraryDbGateway.EnsureBmsonSchema(db);
                if (bmson)
                {
                    db.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(bmsonSong!), typeof(LR2SongDBExtended.bmson_song));
                }
                else
                {
                    db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(bmsFile!), typeof(LR2SongDB.song));
                }

                db.InsertOrReplace(MaintenanceStorageMapping.ToStorage(initialInfo), typeof(LR2SongDBExtended.maintenance));
                if (failMaintenance)
                {
                    // Path relocation retains the old health (zero), so only
                    // the later resource recheck's durable write is rejected.
                    db.Execute("CREATE TRIGGER repair_health_insert BEFORE INSERT ON maintenance WHEN NEW.wav_files_existing = 1 BEGIN SELECT RAISE(ABORT, 'repair-health-write-fault'); END;");
                    db.Execute("CREATE TRIGGER repair_health_update BEFORE UPDATE ON maintenance WHEN NEW.wav_files_existing = 1 BEGIN SELECT RAISE(ABORT, 'repair-health-write-fault'); END;");
                }
            }
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BmsCharts = bmsFile == null ? [] : [bmsFile],
                BmsonCharts = bmsonSong == null ? [] : [bmsonSong]
            };
            ChartFile repairTarget = ChartFileProjection.WithPackageState(chart, destination, string.Empty, string.Empty, []);
            int refreshCount = 0;
            bool notifiedWithLeaseHeld = false;
            bool notifiedWithCurrentHealth = false;
            library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    return;
                }

                refreshCount++;
                using LibraryFileMutationLease probe = library.TryBeginLibraryFileMutation("repair_health_notification_probe");
                notifiedWithLeaseHeld |= probe == null;
                notifiedWithCurrentHealth |= CurrentOwnedChart(library, chart).ResourceHealthMaintenanceSnapshot?.WavFilesExisting == 1;
            };

            LibraryFixInstallationResult? repairResult = null;
            Exception? failure = null;
            try
            {
                repairResult = library.FixInstallationDirectoryCharts([repairTarget]);
                failure = repairResult?.Failure;
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            Assert.IsNotNull(repairResult);
            Assert.IsNotNull(repairResult.SessionReceipt);
            Assert.IsTrue(repairResult.SessionReceipt.DurableCommit);
            Assert.AreEqual(1, repairResult.SessionReceipt.CatalogChartPathChangeCount);
            Assert.AreEqual(destinationPath, CurrentOwnedChart(library, chart).Path);
            Assert.IsFalse(File.Exists(sourcePath));
            Assert.IsTrue(File.Exists(destinationPath));
            Assert.IsTrue(refreshCount > 0);
            Assert.IsFalse(notifiedWithLeaseHeld);
            using LibraryFileMutationLease afterRepair = library.TryBeginLibraryFileMutation("repair_health_completion_probe");
            Assert.IsNotNull(afterRepair);
            if (failMaintenance)
            {
                Assert.IsNotNull(failure);
                Assert.IsNotNull(repairResult.SessionReceipt.FinalizationFailure);
                Assert.AreSame(repairResult.SessionReceipt.FinalizationFailure, failure);
                Exception completedFailure = failure!;
                StringAssert.Contains(completedFailure.ToString(), "repair-health-write-fault");
            }
            else
            {
                Assert.IsNull(failure);
                Assert.IsNull(repairResult.SessionReceipt.FinalizationFailure);
                Assert.IsTrue(notifiedWithCurrentHealth);
                ResourceHealthMaintenanceSnapshot updatedInfo = CurrentOwnedChart(library, chart).ResourceHealthMaintenanceSnapshot;
                Assert.AreEqual(1, updatedInfo.WavFilesDefined);
                Assert.AreEqual(1, updatedInfo.WavFilesExisting);
                Assert.AreEqual(destinationPath, updatedInfo.Path);
                ChartFile installed = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshotWithInstallDestinationOverlay(library).Single();
                Assert.AreEqual(string.Empty, installed.InstallDestination);
                Assert.IsFalse(new BmsLibraryMaintenanceService().BuildResourceHealthWarnings(installed)
                    .Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            }
            using var readback = new LR2SongDBExtended(songDbPath);
            LR2SongDBExtended.maintenance persisted = readback.Table<LR2SongDBExtended.maintenance>().Single(row => row.path == destinationPath);
            Assert.AreEqual(failMaintenance ? 0 : 1, persisted.wav_files_existing);
            Assert.AreEqual(0, readback.Table<LR2SongDBExtended.maintenance>().Count(row => row.path == sourcePath));
        });
    }

    [TestMethod]
    public void FixInstallationDirectoryCharts_BmsChartPreservesExistingLr2SongUserColumns()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_BmsRepair_" + Guid.NewGuid().ToString("N"));
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Broken");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed");
            string sourceChartPath = Path.Combine(sourceDirectoryPath, "chart.bms");
            string destinationChartPath = Path.Combine(destinationDirectoryPath, "chart.bms");
            Directory.CreateDirectory(sourceDirectoryPath);
            Directory.CreateDirectory(destinationDirectoryPath);
            File.WriteAllText(sourceChartPath, "#PLAYER 1\r\n#TITLE repaired\r\n");
            try
            {
                ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(sourceChartPath));
                file = file with { Token = new OwnedChartToken() };
                string hash = file.Md5;
                ChartFile existing = ChartTestValues.Empty() with
                {
                    Path = sourceChartPath,
                    AddDate = 12345,
                    Tag = "external-user-tag"
                };
                existing = existing with { Md5 = hash };
                existing = existing with { Favorite = 7 };
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(existing), typeof(LR2SongDB.song));
                }

                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                library.BmsCharts = [file];
                ChartFile repairTarget = ChartFileProjection.WithPackageState(
                    (file),
                    destinationDirectoryPath,
                    string.Empty,
                    string.Empty,
                    []);

                library.FixInstallationDirectoryCharts([repairTarget]);

                using var verify = new LR2SongDBExtended(songDbPath);
                Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", sourceChartPath));
                LR2SongDB.song row = verify.Table<LR2SongDB.song>().Single(candidate => candidate.path == destinationChartPath);
                Assert.AreEqual(hash, row.hash);
                // 純移転では、利用者列だけでなく旧DB行の未変更の生成列も継承します。
                Assert.AreEqual(string.Empty, row.title);
                Assert.AreEqual("repaired", CurrentOwnedChart(library, file).RawTitle);
                Assert.AreEqual(12345, row.adddate);
                Assert.AreEqual(7, row.favorite);
                Assert.AreEqual("external-user-tag", row.tag);
                Assert.IsFalse(string.IsNullOrWhiteSpace(row.folder));
                Assert.IsFalse(string.IsNullOrWhiteSpace(row.parent));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public void RemoveLibraryCharts_DurableCatalogFailureLeavesConsumerStateUnchanged()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_CatalogFailure_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRootPath);
            string chartPath = Path.Combine(tempRootPath, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1");
            try
            {
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                ChartFile file = ChartTestValues.Empty() with
                {
                    Path = chartPath
                };
                file = file with { Token = new OwnedChartToken() };
                file = file with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                library.BmsCharts = [file];
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                    string escapedPath = chartPath.Replace("'", "''");
                    songDb.Execute(
                        "CREATE TRIGGER fail_library_delta_remove BEFORE DELETE ON song WHEN OLD.path = '"
                        + escapedPath
                        + "' BEGIN SELECT RAISE(ABORT, 'forced library mutation failure'); END;");
                }

                int notificationVersion = library.NormalLibraryRefreshNotificationVersion;
                int ownedCollectionVersion = library.OwnedCollectionVersion;
                ResourceHealthIndexSnapshot resourceHealthSnapshot = library.GetResourceHealthIndexSnapshotForView("failure_baseline");
                LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                    library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((file))]),
                    sendToRecycleBin: false,
                    approvedWholeFolderDeletePaths: []);

                Assert.AreEqual(notificationVersion, library.NormalLibraryRefreshNotificationVersion);
                Assert.AreEqual(ownedCollectionVersion, library.OwnedCollectionVersion);
                Assert.IsTrue(outcome.HasError);
                Assert.IsTrue(outcome.CatalogApplyAttempted);
                Assert.IsFalse(outcome.CatalogDurable);
                Assert.IsNotNull(outcome.CatalogFailure);
                Assert.IsFalse(File.Exists(chartPath));
                ResourceHealthIndexSnapshot resourceHealthAfterFailure = library.TryGetCurrentResourceHealthIndexSnapshotForView();
                Assert.AreSame(resourceHealthSnapshot, resourceHealthAfterFailure);
                Assert.AreEqual(resourceHealthSnapshot.Version, resourceHealthAfterFailure.Version);
                Assert.AreEqual(resourceHealthSnapshot.TargetCount, resourceHealthAfterFailure.TargetCount);
                Assert.AreEqual(resourceHealthSnapshot.NeedFixCount, resourceHealthAfterFailure.NeedFixCount);
                Assert.AreEqual(1, library.BmsCharts.Count);
                Assert.AreSame(file, library.BmsCharts.Single());
                using var verifySongDb = new LR2SongDBExtended(songDbPath);
                Assert.IsTrue(verifySongDb.Table<LR2SongDB.song>().Any(row => row.path == chartPath));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public void RemoveLibraryCharts_CommitsCatalogBeforePublishingOwnedCollectionChange()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartDirectory = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Committed");
            string chartPath = Path.Combine(chartDirectory, "chart.bms");
            Directory.CreateDirectory(chartDirectory);
            File.WriteAllText(chartPath, "#PLAYER 1");
            var library = new TestBmsLibrary(songDbPath);
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            file = file with { Token = new OwnedChartToken() };
            file = file with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
            library.BmsCharts = [file];
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
            }

            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            bool rowStillExistsWhenNotificationWasPublished = false;
            library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName != nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    return;
                }
                using var notificationSongDb = new LR2SongDBExtended(songDbPath);
                rowStillExistsWhenNotificationWasPublished = notificationSongDb.Table<LR2SongDB.song>().Any(row => row.path == chartPath);
            };
            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((file))]),
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: [chartDirectory]);

            Assert.IsFalse(outcome.HasError);
            Assert.AreEqual(new LibraryMutationSessionApplyCounts
            {
                CatalogApplyCount = 1,
                PackageReferenceApplyCount = 1,
                ReverseLookupApplyCount = 1,
                Lr2SyncCount = 1,
                RequiredPublicationCount = 1
            }, outcome.SessionReceipt.ApplyCounts);
            Assert.AreEqual(0, library.BmsCharts.Count);
            NormalLibraryRefreshNotificationBatch notificationBatch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            Assert.IsTrue((notificationBatch.ChangedCharts.Count > 0 || notificationBatch.DeletedTokens.Count > 0 || notificationBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged)));
            Assert.IsFalse(rowStillExistsWhenNotificationWasPublished);
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            Assert.IsFalse(verifySongDb.Table<LR2SongDB.song>().Any(row => row.path == chartPath));
        });
    }

    [TestMethod]
    public void RemoveLibraryCharts_PublicNotificationFailureKeepsCatalogCommit()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartDirectory = Path.Combine(Path.GetDirectoryName(songDbPath)!, "CommittedNotificationFailure");
            string chartPath = Path.Combine(chartDirectory, "chart.bms");
            Directory.CreateDirectory(chartDirectory);
            File.WriteAllText(chartPath, "#PLAYER 1");
            var library = new TestBmsLibrary(songDbPath);
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            file = file with { Token = new OwnedChartToken() };
            file = file with { Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" };
            library.BmsCharts = [file];
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
            }

            bool notificationAttempted = false;
            library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(BMSLibrary.OwnedCollectionVersion))
                {
                    notificationAttempted = true;
                    throw new InvalidOperationException("public notification failure");
                }
            };
            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((file))]),
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: [chartDirectory]);

            Assert.IsTrue(notificationAttempted);
            Assert.IsFalse(outcome.HasError);
            Assert.AreEqual(0, library.BmsCharts.Count);
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            Assert.IsFalse(verifySongDb.Table<LR2SongDB.song>().Any(row => row.path == chartPath));
        });
    }

    [TestMethod]
    public void RenameChartFolder_BuiltInstalledLookupMovesPathIncrementally()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_LookupMove_" + Guid.NewGuid().ToString("N"));
            string oldDirectoryPath = Path.Combine(tempRootPath, "Old");
            string newDirectoryPath = Path.Combine(tempRootPath, "New");
            string oldChartPath = Path.Combine(oldDirectoryPath, "chart.bms");
            string newChartPath = Path.Combine(newDirectoryPath, "chart.bms");
            Directory.CreateDirectory(oldDirectoryPath);
            File.WriteAllText(oldChartPath, "#PLAYER 1");
            try
            {
                string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                ChartFile file = ChartTestValues.Empty() with
                {
                    Path = oldChartPath
                };
                file = file with { Token = new OwnedChartToken() };
                file = file with { Md5 = hash };
                library.BmsCharts = [file];
                InstalledChartLookupIndexSnapshot initial = InvokeCreateInstalledChartLookupSnapshot(library);
                Assert.IsTrue(IsInstalledChartLookupIndexInitialized(library));
                CollectionAssert.AreEqual(new[] { oldDirectoryPath }, initial.Md5Directories[hash].ToArray());

                LibraryMutationSessionReceipt receipt = library.RenameChartFolderWithReceipt(
                    oldDirectoryPath,
                    "New",
                    unregister: false,
                    renameRootFolder: false);
                Assert.IsTrue(receipt.DurableCommit, receipt.PrimaryFailure?.ToString());
                InstalledChartLookupIndexSnapshot updated = InvokeCreateInstalledChartLookupSnapshot(library);

                Assert.IsTrue(IsInstalledChartLookupIndexInitialized(library));
                Assert.AreEqual(newChartPath, CurrentOwnedChart(library, file).Path);
                CollectionAssert.AreEqual(new[] { newDirectoryPath }, updated.Md5Directories[hash].ToArray());
                Assert.IsFalse(updated.KnownChartDirectories.Contains(oldDirectoryPath));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public void BMSFilesReplacement_InvalidatesBuiltInstalledLookup()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string firstDirectoryPath = Path.Combine("C:\\Installed", "First");
            string secondDirectoryPath = Path.Combine("C:\\Installed", "Second");
            string firstHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            string secondHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            ChartFile firstFile = ChartTestValues.Empty() with
            {
                Path = Path.Combine(firstDirectoryPath, "chart.bms")
            };
            firstFile = firstFile with { Token = new OwnedChartToken() };
            firstFile = firstFile with { Md5 = firstHash };
            ChartFile secondFile = ChartTestValues.Empty() with
            {
                Path = Path.Combine(secondDirectoryPath, "chart.bms")
            };
            secondFile = secondFile with { Token = new OwnedChartToken() };
            secondFile = secondFile with { Md5 = secondHash };

            library.BmsCharts = [firstFile];
            InstalledChartLookupIndexSnapshot initial = InvokeCreateInstalledChartLookupSnapshot(library);
            Assert.IsTrue(IsInstalledChartLookupIndexInitialized(library));
            Assert.IsTrue(initial.ContainsPrimaryHash(firstHash));

            library.BmsCharts = [secondFile];

            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));
            InstalledChartLookupIndexSnapshot rebuilt = InvokeCreateInstalledChartLookupSnapshot(library);
            Assert.IsFalse(rebuilt.ContainsPrimaryHash(firstHash));
            Assert.IsTrue(rebuilt.ContainsPrimaryHash(secondHash));
            CollectionAssert.AreEqual(new[] { secondDirectoryPath }, rebuilt.Md5Directories[secondHash].ToArray());
        });
    }

    [TestMethod]
    public void FixInstallationDirectoryCharts_UsesSuccessfulRepairHashOverlayForLaterDuplicate()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RepairHashOverlay_" + Guid.NewGuid().ToString("N"));
            string firstSourceDirectory = Path.Combine(tempRootPath, "First");
            string secondSourceDirectory = Path.Combine(tempRootPath, "Second");
            string destinationDirectory = Path.Combine(tempRootPath, "Installed");
            Directory.CreateDirectory(firstSourceDirectory);
            Directory.CreateDirectory(secondSourceDirectory);
            Directory.CreateDirectory(destinationDirectory);
            string firstSourcePath = Path.Combine(firstSourceDirectory, "chart.bms");
            string secondSourcePath = Path.Combine(secondSourceDirectory, "chart.bms");
            string destinationPath = Path.Combine(destinationDirectory, "chart.bms");
            const string chartText = "#PLAYER 1\r\n#TITLE Session overlay\r\n#00111:0100\r\n";
            File.WriteAllText(firstSourcePath, chartText);
            File.WriteAllText(secondSourcePath, chartText);
            try
            {
                ChartFile first = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(firstSourcePath));
                first = first with { Token = new OwnedChartToken() };
                ChartFile second = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(secondSourcePath));
                second = second with { Token = new OwnedChartToken() };
                Assert.AreEqual(first.Md5, second.Md5);
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(first), typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(second), typeof(LR2SongDB.song));
                }
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
                {
                    BmsCharts = [first, second]
                };
                ChartFile firstTarget = ChartFileProjection.WithPackageState(
                    (first), destinationDirectory, string.Empty, string.Empty, []);
                ChartFile secondTarget = ChartFileProjection.WithPackageState(
                    (second), destinationDirectory, string.Empty, string.Empty, []);

                LibraryFixInstallationResult result = library.FixInstallationDirectoryCharts(
                    [firstTarget, secondTarget],
                    approvedDuplicateRemovalChartPaths: []);

                Assert.IsNotNull(result.SessionReceipt);
                Assert.IsTrue(result.SessionReceipt.DurableCommit, result.SessionReceipt.PrimaryFailure?.ToString());
                Assert.AreEqual(1, result.MovedCount);
                Assert.AreEqual(1, result.DuplicateSkippedCount);
                Assert.AreEqual(1, result.SessionReceipt.ConfirmedChangeCount);
                Assert.AreEqual(1, result.SessionReceipt.CatalogChartPathChangeCount);
                Assert.AreEqual(1, result.SessionReceipt.PackageInstallDestinationChangeCount);
                Assert.IsFalse(File.Exists(firstSourcePath));
                Assert.IsTrue(File.Exists(destinationPath));
                Assert.IsTrue(File.Exists(secondSourcePath));
                Assert.AreEqual(destinationPath, CurrentOwnedChart(library, first).Path);
                Assert.AreEqual(secondSourcePath, CurrentOwnedChart(library, second).Path);
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [DataRow(false, false)]
    public void FixInstallationDirectoryCharts_BmsonDuplicateHonorsApprovalAndKeepsSibling(
        bool catalogFailure,
        bool approveDuplicateRemoval)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_BmsonRepairDup_" + Guid.NewGuid().ToString("N"));
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Broken");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed");
            string sourceChartPath = Path.Combine(sourceDirectoryPath, "chart.bmson");
            string installedChartPath = Path.Combine(destinationDirectoryPath, "chart.bmson");
            Directory.CreateDirectory(sourceDirectoryPath);
            Directory.CreateDirectory(destinationDirectoryPath);
            File.WriteAllText(sourceChartPath, "{}");
            File.WriteAllText(installedChartPath, "{}");
            try
            {
                string md5 = "abcdefabcdefabcdefabcdefabcdefab";
                string sha256 = "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd";
                ChartFile sourceSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
                {
                    Path = sourceChartPath,
                    Folder = sourceDirectoryPath,
                    RawTitle = "Repair Duplicate Bmson",
                    Md5 = md5,
                    Sha256 = sha256
                };
                sourceSong = sourceSong with { Token = new OwnedChartToken() };
                ChartFile installedSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
                {
                    Path = installedChartPath,
                    Folder = destinationDirectoryPath,
                    RawTitle = "Installed Duplicate Bmson",
                    Md5 = md5,
                    Sha256 = sha256
                };
                installedSong = installedSong with { Token = new OwnedChartToken() };
                string movedSource = Path.Combine(sourceDirectoryPath, "other.bmson");
                string movedDestination = Path.Combine(destinationDirectoryPath, "other.bmson");
                File.WriteAllText(movedSource, "{}");
                string siblingSourcePath = Path.Combine(sourceDirectoryPath, "sibling.bmson");
                File.WriteAllText(siblingSourcePath, "{}");
                ChartFile movedSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
                {
                    Path = movedSource,
                    Folder = sourceDirectoryPath,
                    RawTitle = "Independent repair target",
                    Md5 = "11111111111111111111111111111111",
                    Sha256 = "1111111111111111111111111111111111111111111111111111111111111111"
                };
                movedSong = movedSong with { Token = new OwnedChartToken() };
                ChartFile siblingSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
                {
                    Path = siblingSourcePath,
                    Folder = sourceDirectoryPath,
                    RawTitle = "Unselected sibling",
                    Md5 = "22222222222222222222222222222222",
                    Sha256 = "2222222222222222222222222222222222222222222222222222222222222222"
                };
                siblingSong = siblingSong with { Token = new OwnedChartToken() };
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(sourceSong), typeof(LR2SongDBExtended.bmson_song));
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(installedSong), typeof(LR2SongDBExtended.bmson_song));
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(movedSong), typeof(LR2SongDBExtended.bmson_song));
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(siblingSong), typeof(LR2SongDBExtended.bmson_song));
                    if (catalogFailure)
                    {
                        songDb.Execute("CREATE TRIGGER fail_repair_delete BEFORE DELETE ON bmson_song WHEN OLD.path = '"
                            + sourceChartPath.Replace("'", "''") + "' BEGIN SELECT RAISE(ABORT, 'repair-deletion-fault'); END;");
                    }
                }
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                library.BmsonCharts = [sourceSong, installedSong, movedSong, siblingSong];
                ChartFile repairTarget = ChartFileProjection.WithPackageState(
                    (sourceSong),
                    destinationDirectoryPath,
                    string.Empty,
                    string.Empty,
                    []);

                ChartFile independentTarget = ChartFileProjection.WithPackageState(
                    (movedSong), destinationDirectoryPath, string.Empty, string.Empty, []);
                IReadOnlyList<string> approvedPaths = approveDuplicateRemoval ? [sourceChartPath] : [];
                LibraryFixInstallationResult repairResult = library.FixInstallationDirectoryCharts(
                    [independentTarget, repairTarget],
                    approvedPaths);
                Assert.IsNotNull(repairResult.SessionReceipt);
                Assert.AreEqual(1, repairResult.DuplicateSkippedCount);
                Assert.AreEqual(1, repairResult.SessionReceipt.CatalogChartPathChangeCount);
                if (approveDuplicateRemoval)
                {
                    Assert.AreEqual(1, repairResult.ApprovedRemovedCount);
                    Assert.AreEqual(1, repairResult.SessionReceipt.CatalogChartRemovalCount);
                    Assert.AreEqual(2, repairResult.SessionReceipt.ConfirmedChangeCount);
                    if (catalogFailure)
                    {
                        Assert.IsNotNull(repairResult.Failure);
                        Assert.IsFalse(repairResult.SessionReceipt.DurableCommit);
                        Assert.IsNotNull(repairResult.SessionReceipt.ApplyFailure);
                    }
                    else
                    {
                        Assert.IsNull(repairResult.Failure);
                        Assert.IsTrue(repairResult.SessionReceipt.DurableCommit);
                    }
                }
                else
                {
                    Assert.IsNull(repairResult.Failure);
                    Assert.AreEqual(0, repairResult.ApprovedRemovedCount);
                    Assert.AreEqual(0, repairResult.SessionReceipt.CatalogChartRemovalCount);
                    Assert.AreEqual(1, repairResult.SessionReceipt.ConfirmedChangeCount);
                }
                Assert.IsFalse(File.Exists(movedSource));
                Assert.IsTrue(File.Exists(movedDestination));

                Assert.AreEqual(approveDuplicateRemoval, !File.Exists(sourceChartPath));
                Assert.IsTrue(File.Exists(installedChartPath));
                Assert.IsTrue(File.Exists(siblingSourcePath));
                Assert.AreEqual(!approveDuplicateRemoval || catalogFailure, library.BmsonCharts.Any(song => string.Equals(song.Path, sourceChartPath, StringComparison.OrdinalIgnoreCase)));
                Assert.IsTrue(library.BmsonCharts.Any(song => string.Equals(song.Path, installedChartPath, StringComparison.OrdinalIgnoreCase)));
                Assert.IsTrue(library.BmsonCharts.Any(song => string.Equals(song.Path, siblingSourcePath, StringComparison.OrdinalIgnoreCase)));
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    Assert.AreEqual(approveDuplicateRemoval && catalogFailure ? 1 : approveDuplicateRemoval ? 0 : 1,
                        songDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == sourceChartPath));
                    Assert.AreEqual(approveDuplicateRemoval && catalogFailure ? 0 : 1,
                        songDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == movedDestination));
                    Assert.AreEqual(approveDuplicateRemoval && catalogFailure ? 1 : 0,
                        songDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == movedSource));
                    Assert.AreEqual(1, songDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == installedChartPath));
                    Assert.AreEqual(1, songDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == siblingSourcePath));
                }
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public void SetBMSFilesEncoding_UpdatesEncodingCellWithoutLibraryCollectionChanged()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_EncodingRefresh_" + Guid.NewGuid().ToString("N"));
            string chartPath = Path.Combine(tempRootPath, "chart.bms");
            Directory.CreateDirectory(tempRootPath);
            File.WriteAllText(
                chartPath,
                "#TITLE Garbled\r\n#ARTIST Artist\r\n#GENRE TEST\r\n",
                Encoding.GetEncoding("gb2312", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
            try
            {
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
                file = file with { Token = new OwnedChartToken() };
                file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = file.Path, hash = file.Md5, encoding = "unknown", is_encoding_fixed = false }));
                int garbledChangedCount = 0;
                int garbledFixedChangedCount = 0;
                int encodingChangedCount = 0;
                library.PropertyChanged += delegate (object? sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSLibrary.ChartFilesGarbled))
                    {
                        Interlocked.Increment(ref garbledChangedCount);
                    }
                    if (e.PropertyName == nameof(BMSLibrary.ChartFilesGarbledFixed))
                    {
                        Interlocked.Increment(ref garbledFixedChangedCount);
                    }
                };
                var rowCache = new NormalLibraryRowCache();
                LibraryChartRow? row = null;
                int handledVersion = library.NormalLibraryRefreshNotificationVersion;
                library.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion) && row is not null)
                    {
                        NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledVersion);
                        rowCache.ApplyChanges(batch.ChangedCharts, batch.DeletedTokens, null);
                        handledVersion = batch.LatestVersion;
                    }
                };
                library.BmsCharts = [file];
                file = library.BmsCharts.Single();
                row = rowCache.GetOrCreate(file, null);
                row.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(LibraryChartRow.encoding) || string.IsNullOrEmpty(args.PropertyName))
                    {
                        Interlocked.Increment(ref encodingChangedCount);
                    }
                };
                handledVersion = library.NormalLibraryRefreshNotificationVersion;
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(new LR2SongDBExtended.maintenance { path = chartPath, hash = file.Md5, encoding = "unknown", is_encoding_fixed = false }, typeof(LR2SongDBExtended.maintenance));
                }
                Interlocked.Exchange(ref garbledChangedCount, 0);
                Interlocked.Exchange(ref garbledFixedChangedCount, 0);
                Interlocked.Exchange(ref encodingChangedCount, 0);

                library.SetBMSFilesEncoding([file], "gb2312");

                Assert.AreEqual(0, Volatile.Read(ref garbledChangedCount));
                Assert.AreEqual(0, Volatile.Read(ref garbledFixedChangedCount));
                Assert.IsTrue(Volatile.Read(ref encodingChangedCount) > 0);
                Assert.AreEqual("unknown", file.ResourceHealthMaintenanceSnapshot.Encoding);
                Assert.AreEqual("gb2312", row.encoding);
                Assert.AreSame(file.Token, row.Chart.Token);
                using var verifySongDb = new LR2SongDBExtended(songDbPath);
                LR2SongDBExtended.maintenance persistedMaintenance = verifySongDb.Table<LR2SongDBExtended.maintenance>().Single(row => row.path == chartPath);
                Assert.AreEqual("gb2312", persistedMaintenance.encoding);
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public async Task RefreshReferenceDisplayForTable_UpdatesPlaylistCellWithoutStorageRowCollectionNotification()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        await WithTemporarySongDbAsync(async delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = @"C:\Library\chart.bms"
            };
            file = file with { Token = new OwnedChartToken() };
            file = file with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
            var bmsFilesPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var table = new BMSTable
            {
                name = "Before",
                symbol = "A",
                entries = [new BMSTableEntry(file)]
            };
            int bmsFilesChangedCount = 0;
            ChartFile capturedBeforeReferenceRefresh = file;
            library.PropertyChanged += delegate (object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    Interlocked.Increment(ref bmsFilesChangedCount);
                    bmsFilesPublished.TrySetResult(true);
                }
            };
            library.BmsCharts = [file];
            await bmsFilesPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Interlocked.Exchange(ref bmsFilesChangedCount, 0);
            library.RefreshReferenceDisplayForTable(table);
            ChartFile chart = (file);

            table.symbol = "B";
            table.name = "After";
            Assert.AreEqual("A", library.GetPlaylistReferenceDisplay(chart).Symbols);
            Assert.AreEqual("Before", library.GetPlaylistReferenceDisplay(chart).Names);

            library.RefreshReferenceDisplayForTable(table);

            Assert.AreSame(capturedBeforeReferenceRefresh, file);
            Assert.AreEqual(0, Volatile.Read(ref bmsFilesChangedCount));
            Assert.AreEqual("B", library.GetPlaylistReferenceDisplay(chart).Symbols);
            Assert.AreEqual("After", library.GetPlaylistReferenceDisplay(chart).Names);
        });
    }

    [TestMethod]
    public void SynchronizeReferenceBMSTables_ReplacesReloadedPlaylistReferenceWithoutAppending()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = @"C:\Library\chart.bms"
            };
            file = file with { Token = new OwnedChartToken() };
            file = file with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
            BMSTable oldTable = CreateTable("Before", "A", file.Md5);
            BMSTable newTable = CreateTable("After", "B", file.Md5);
            library.BmsCharts = [file];

            library.AddReferenceBMSTables(oldTable);
            ChartFile chart = (file);
            Assert.AreEqual("A", library.GetPlaylistReferenceDisplay(chart).Symbols);
            Assert.AreEqual("Before", library.GetPlaylistReferenceDisplay(chart).Names);

            library.SynchronizeReferenceBMSTables([newTable]);

            Assert.AreEqual("B", library.GetPlaylistReferenceDisplay(chart).Symbols);
            Assert.AreEqual("After", library.GetPlaylistReferenceDisplay(chart).Names);
        });
    }

    [TestMethod]
    public void PreparedReferenceSynchronizationCommitsAtomicallyAndRejectsStaleRevision()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = @"C:\Library\chart.bms"
            };
            file = file with { Token = new OwnedChartToken() };
            file = file with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
            BMSTable currentTable = CreateTable("Before", "A", file.Md5);
            BMSTable replacementTable = CreateTable("After", "B", file.Md5);
            library.BmsCharts = [file];
            library.AddReferenceBMSTables(currentTable);
            ChartFile chart = (file);

            BmsLibraryPlaylistReferenceOwner.PlaylistReferenceSynchronizationPlan stalePlan = library.PrepareReferenceBMSTableSynchronization([replacementTable]);
            Assert.AreEqual("A", library.GetPlaylistReferenceDisplay(chart).Symbols);

            currentTable.symbol = "C";
            currentTable.name = "Concurrent";
            library.RefreshReferenceDisplayForTable(currentTable);

            Assert.IsFalse(library.TryCommitReferenceBMSTableSynchronization(stalePlan));
            Assert.AreEqual("C", library.GetPlaylistReferenceDisplay(chart).Symbols);
            Assert.AreEqual("Concurrent", library.GetPlaylistReferenceDisplay(chart).Names);

            BmsLibraryPlaylistReferenceOwner.PlaylistReferenceSynchronizationPlan currentPlan = library.PrepareReferenceBMSTableSynchronization([replacementTable]);
            Assert.IsTrue(library.TryCommitReferenceBMSTableSynchronization(currentPlan));
            Assert.AreEqual("B", library.GetPlaylistReferenceDisplay(chart).Symbols);
            Assert.AreEqual("After", library.GetPlaylistReferenceDisplay(chart).Names);
        });
    }

    [TestMethod]
    public void AddReferenceBMSTables_DoesNotMaterializeUnmatchedPendingBmsonEntries()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            PackageChartEntry adapterlessBmsonEntry = CreateAdapterlessBmsonEntry(
                @"C:\Pending\Package\chart.bmson",
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            library.ChartPackagesPending = CreatePackageCollection(
            [
                ChartPackage.FromChartEntries([adapterlessBmsonEntry])
            ]);
            BMSTable table = CreateTable("Unmatched", "U", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

            library.AddReferenceBMSTables(table);

            Assert.IsNull(adapterlessBmsonEntry.GetBmsChartForTest());
        });
    }

    [TestMethod]
    public void AddReferenceBMSTables_UsesPlaylistIndexForMatchedPendingBmsonWithoutMaterializing()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            string matchingHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            PackageChartEntry matchingBmsonEntry = CreateAdapterlessBmsonEntry(
                @"C:\Pending\Package\matching.bmson",
                matchingHash);
            PackageChartEntry unmatchedBmsonEntry = CreateAdapterlessBmsonEntry(
                @"C:\Pending\Package\unmatched.bmson",
                "cccccccccccccccccccccccccccccccc");
            library.ChartPackagesPending = CreatePackageCollection(
            [
                ChartPackage.FromChartEntries([matchingBmsonEntry, unmatchedBmsonEntry])
            ]);
            BMSTable table = CreateTable("Matched", "M", matchingHash);

            library.AddReferenceBMSTables(table);

            Assert.IsNull(matchingBmsonEntry.GetBmsChartForTest());
            Assert.IsNull(unmatchedBmsonEntry.GetBmsChartForTest());
            Assert.AreEqual("M", library.GetPlaylistReferenceDisplay(matchingBmsonEntry.Chart).Symbols);
            Assert.AreEqual("Matched", library.GetPlaylistReferenceDisplay(matchingBmsonEntry.Chart).Names);
            Assert.AreEqual(string.Empty, library.GetPlaylistReferenceDisplay(unmatchedBmsonEntry.Chart).Symbols);
        });
    }

    [TestMethod]
    public void AddReferenceBMSTables_UsesPlaylistIndexForBmsonWithoutBmsOwner()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            string matchingHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = @"C:\Pending\Package\matching.bmson",
                Md5 = matchingHash,
                Sha256 = new string('b', 64),
                RawTitle = "Pending Bmson",
                RawArtist = "Artist"
            };
            var matchingBmsonEntry = PackageChartEntry.FromChart((song));
            library.ChartPackagesPending = CreatePackageCollection(
            [
                ChartPackage.FromChartEntries([matchingBmsonEntry])
            ]);
            BMSTable table = CreateTable("Matched", "M", matchingHash);

            library.AddReferenceBMSTables(table);

            Assert.IsNull(matchingBmsonEntry.GetBmsChartForTest());
            Assert.AreEqual("M", library.GetPlaylistReferenceDisplay(matchingBmsonEntry.Chart).Symbols);
            Assert.AreEqual("Matched", library.GetPlaylistReferenceDisplay(matchingBmsonEntry.Chart).Names);
        });
    }

    [TestMethod]
    public void AddReferenceBMSTables_UsesPlaylistIndexForInstalledBmsonWithoutBmsOwner()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            string matchingHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = @"C:\Library\chart.bmson",
                Md5 = matchingHash,
                Sha256 = new string('b', 64),
                RawTitle = "Installed Bmson",
                RawArtist = "Artist"
            };
            song = song with { Token = new OwnedChartToken() };
            library.BmsonCharts = [song];
            ChartFile chart = (song);
            BMSTable table = CreateTable("Matched", "M", matchingHash);

            library.AddReferenceBMSTables(table);

            Assert.IsNotNull(chart.Token);
            Assert.AreSame(song.Token, chart.Token);
            Assert.AreEqual("M", library.GetPlaylistReferenceDisplay(chart).Symbols);
            Assert.AreEqual("Matched", library.GetPlaylistReferenceDisplay(chart).Names);
        });
    }

    [TestMethod]
    public void AddReferenceBMSTablesToCharts_UsesPlaylistIndexForBmsStorageOwner()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = @"C:\Library\chart.bms"
            };
            file = file with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
            BMSTable table = CreateTable("Matched", "M", file.Md5);

            library.AddReferenceBMSTablesToCharts(table, [(file)]);

            ChartFile chart = (file);
            Assert.AreEqual("M", library.GetPlaylistReferenceDisplay(chart).Symbols);
            Assert.AreEqual("Matched", library.GetPlaylistReferenceDisplay(chart).Names);
        });
    }

    [TestMethod]
    public void AddReferenceBMSTablesToCharts_UsesPlaylistIndexForBmsonWithoutBmsOwner()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            string matchingHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = @"C:\Library\chart.bmson",
                Md5 = matchingHash,
                Sha256 = new string('b', 64),
                RawTitle = "Library Bmson",
                RawArtist = "Artist"
            };
            ChartFile chart = (song);
            BMSTable table = CreateTable("Matched", "M", matchingHash);

            library.AddReferenceBMSTablesToCharts(table, [chart]);

            Assert.IsNull(chart.Token);
            Assert.AreSame(song.Token, chart.Token);
            Assert.AreEqual("M", library.GetPlaylistReferenceDisplay(chart).Symbols);
            Assert.AreEqual("Matched", library.GetPlaylistReferenceDisplay(chart).Names);
        });
    }

    [TestMethod]
    public void AddReferenceBMSTablesToPackageCharts_DoesNotMaterializeUnmatchedBmsonEntries()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            PackageChartEntry adapterlessBmsonEntry = CreateAdapterlessBmsonEntry(
                @"C:\Installed\Package\unmatched.bmson",
                "cccccccccccccccccccccccccccccccc");
            var installedPackage = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
            BMSTable table = CreateTable("Unmatched", "U", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

            library.AddReferenceBMSTablesToPackageCharts([table], [installedPackage]);

            Assert.IsNull(adapterlessBmsonEntry.GetBmsChartForTest());
        });
    }

    [TestMethod]
    public void AddReferenceBMSTablesToPackageCharts_UsesPlaylistIndexForMatchedBmsonWithoutMaterializing()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            string matchingHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            PackageChartEntry matchingBmsonEntry = CreateAdapterlessBmsonEntry(
                @"C:\Installed\Package\matching.bmson",
                matchingHash);
            PackageChartEntry unmatchedBmsonEntry = CreateAdapterlessBmsonEntry(
                @"C:\Installed\Package\unmatched.bmson",
                "cccccccccccccccccccccccccccccccc");
            var installedPackage = ChartPackage.FromChartEntries([matchingBmsonEntry, unmatchedBmsonEntry]);
            BMSTable table = CreateTable("Matched", "M", matchingHash);
            library.AddReferenceBMSTables([table]);

            library.AddReferenceBMSTablesToPackageCharts([table], [installedPackage]);

            Assert.IsNull(matchingBmsonEntry.GetBmsChartForTest());
            Assert.IsNull(unmatchedBmsonEntry.GetBmsChartForTest());
            Assert.AreEqual("M", library.GetPlaylistReferenceDisplay(matchingBmsonEntry.Chart).Symbols);
            Assert.AreEqual("Matched", library.GetPlaylistReferenceDisplay(matchingBmsonEntry.Chart).Names);
            Assert.AreEqual(string.Empty, library.GetPlaylistReferenceDisplay(unmatchedBmsonEntry.Chart).Symbols);
        });
    }

    [TestMethod]
    public void ReplaceReferenceBMSTable_DoesNotAddNewTableToOldOnlyPendingBmsonEntry()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            string oldHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            string newHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            PackageChartEntry oldOnlyBmsonEntry = CreateAdapterlessBmsonEntry(
                @"C:\Pending\Package\old.bmson",
                oldHash);
            library.ChartPackagesPending = CreatePackageCollection(
            [
                ChartPackage.FromChartEntries([oldOnlyBmsonEntry])
            ]);
            BMSTable oldTable = CreateTable("Old", "O", oldHash);
            BMSTable newTable = CreateTable("New", "N", newHash);
            library.AddReferenceBMSTables(oldTable);
            Assert.IsNull(oldOnlyBmsonEntry.GetBmsChartForTest());
            Assert.AreEqual("O", library.GetPlaylistReferenceDisplay(oldOnlyBmsonEntry.Chart).Symbols);

            library.ReplaceReferenceBMSTable(oldTable, newTable);

            Assert.IsNull(oldOnlyBmsonEntry.GetBmsChartForTest());
            Assert.AreEqual(string.Empty, library.GetPlaylistReferenceDisplay(oldOnlyBmsonEntry.Chart).Symbols);
        });
    }

    [TestMethod]
    public void SynchronizeReferenceBMSTables_DoesNotMaterializeUnmatchedPendingBmsonEntries()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            PackageChartEntry adapterlessBmsonEntry = CreateAdapterlessBmsonEntry(
                @"C:\Pending\Package\unmatched.bmson",
                "cccccccccccccccccccccccccccccccc");
            library.ChartPackagesPending = CreatePackageCollection(
            [
                ChartPackage.FromChartEntries([adapterlessBmsonEntry])
            ]);
            BMSTable table = CreateTable("Unmatched", "U", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

            library.SynchronizeReferenceBMSTables([table]);

            Assert.IsNull(adapterlessBmsonEntry.GetBmsChartForTest());
        });
    }

    [TestMethod]
    public void RemoveReferenceBMSTables_WithEntriesRemovesPendingReferenceMatchedBySha256()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            string sha256 = new string('d', 64);
            PackageChartEntry matchingBmsonEntry = CreateAdapterlessBmsonEntry(
                @"C:\Pending\Package\sha.bmson",
                md5: string.Empty,
                sha256: sha256);
            library.ChartPackagesPending = CreatePackageCollection(
            [
                ChartPackage.FromChartEntries([matchingBmsonEntry])
            ]);
            BMSTable table = CreateTableWithHashes("Sha", "S", md5: string.Empty, sha256: sha256);
            library.AddReferenceBMSTables(table);
            Assert.IsNull(matchingBmsonEntry.GetBmsChartForTest());
            Assert.AreEqual("S", library.GetPlaylistReferenceDisplay(matchingBmsonEntry.Chart).Symbols);

            List<BMSTableEntry> removedEntries = [.. table.entries];
            table.entries.Clear();
            library.RemoveReferenceBMSTables(table, removedEntries);

            Assert.IsNull(matchingBmsonEntry.GetBmsChartForTest());
            Assert.AreEqual(string.Empty, library.GetPlaylistReferenceDisplay(matchingBmsonEntry.Chart).Symbols);
        });
    }

    [TestMethod]
    public void RemoveReferenceBMSTables_AllowsUnloadedTables()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = @"C:\Library\chart.bms"
            };
            file = file with { Token = new OwnedChartToken() };
            string md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
            file = file with { Md5 = md5 };
            library.BmsCharts = [file];
            BMSTable table = CreateTable("Reference", "R", md5);

            library.AddReferenceBMSTables(table);
            ChartFile chart = (file);
            Assert.AreEqual("R", library.GetPlaylistReferenceDisplay(chart).Symbols);

            table.MarkEntriesNotLoaded();
            library.RemoveReferenceBMSTables([table]);

            Assert.AreEqual(string.Empty, library.GetPlaylistReferenceDisplay(chart).Symbols);
        });
    }

    private static InstalledChartLookupIndexSnapshot InvokeCreateInstalledChartLookupSnapshot(BMSLibrary library)
    {
        return library.CreateInstalledChartLookupSnapshotForDiagnostics();
    }

    private static LibraryResourceIndexOwner GetLibraryResourceIndexOwner(BMSLibrary library)
    {
        FieldInfo field = typeof(BMSLibrary).GetField(
            "libraryResourceIndexOwner",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (LibraryResourceIndexOwner)field.GetValue(library)!;
    }

    private static bool IsInstalledChartLookupIndexInitialized(BMSLibrary library)
    {
        return library.IsInstalledChartLookupIndexInitializedForDiagnostics();
    }

    private static List<ChartFile> InvokeCreateOwnedChartInfoFullBackfillTargetSnapshotWithInstallDestinationOverlay(BMSLibrary library)
    {
        return library.CreateOwnedChartInfoFullBackfillTargetSnapshotWithInstallDestinationOverlayForDiagnostics();
    }

    private static List<ChartFile> InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(BMSLibrary library)
    {
        return library.CreateOwnedChartInfoFullBackfillTargetSnapshotForDiagnostics();
    }

    private static ObservableCollection<ChartPackage> CreatePackageCollection(IEnumerable<ChartPackage> packages)
    {
        return new ObservableCollection<ChartPackage>([.. (packages ?? [])]);
    }

    private static PackageChartEntry CreateAdapterlessBmsonEntry(string path, string md5, string sha256 = "")
    {
        return PackageChartEntry.FromChart((ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = path,
            Md5 = md5,
            Sha256 = string.IsNullOrWhiteSpace(sha256) ? new string('b', 64) : sha256,
            RawTitle = "Pending Bmson",
            RawArtist = "Artist"
        }));
    }

    /// <summary>捕捉後に移転した所持項目の共通現在値を、同じ短命識別で検査します。</summary>
    private static ChartFile CurrentOwnedChart(BMSLibrary library, ChartFile captured)
        => library.BmsCharts.Concat(library.BmsonCharts).Single(chart => ReferenceEquals(chart.Token, captured.Token));

    private static void WithTemporarySongDb(Action<string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_FolderRenameRefresh_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            // fixture schemaは初期化処理の検証対象ではないため、一つのtransactionにまとめて共通DBロックの保持時間を短縮する。
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
            });
            testAction(songDbPath);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    private static async Task WithTemporarySongDbAsync(Func<string, Task> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SongDbTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            // asyncテスト用fixture schemaも本番処理の検証対象ではないため、一つのtransactionにまとめて共通DBロックの保持時間を短縮する。
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
            });
            await testAction(songDbPath).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }


    private static BMSTable CreateTable(string name, string symbol, string hash)
    {
        return CreateTableWithHashes(name, symbol, hash, sha256: string.Empty);
    }

    private static BMSTable CreateTableWithHashes(string name, string symbol, string md5, string sha256)
    {
        var bMSTable = new BMSTable
        {
            name = name,
            symbol = symbol
        };
        ChartFile testableBmsFile = ChartTestValues.Empty();
        testableBmsFile = testableBmsFile with { Md5 = md5 };
        testableBmsFile = testableBmsFile with { Sha256 = sha256 };
        testableBmsFile = testableBmsFile with { Path = @"C:\Library\chart.bms" };
        var entry = new BMSTableEntry(testableBmsFile)
        {
            is_removed = false
        };
        if (string.IsNullOrWhiteSpace(md5) && !string.IsNullOrWhiteSpace(sha256))
        {
            entry.MarkAsBmsonPlaylistIdentity(sha256);
        }
        bMSTable.entries.Add(entry);
        return bMSTable;
    }

    private sealed class RecordingDialogService : IBmsLibraryDialogService
    {
        private readonly List<string>? phases;

        internal RecordingDialogService(List<string>? phases = null)
        {
            this.phases = phases;
        }

        internal int CallCount { get; private set; }

        public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            CallCount++;
            phases?.Add("dialog");
            return defaultResult == MessageBoxResult.None ? MessageBoxResult.OK : defaultResult;
        }
    }

    private sealed class TestFileMutationService : IFileMutationService
    {
        public string? MoveDirectoryFailureSourcePath { get; set; }

        public HashSet<string> MoveFileFailureSourcePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Action<string>? OperationObserver { get; set; }

        public string? DeleteDirectoryFailurePath { get; set; }

        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null!)
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            OperationObserver?.Invoke("filesystem");
            if (MoveFileFailureSourcePaths.Contains(sourcePath))
            {
                throw new IOException("Synthetic file move failure for session terminal aggregation test.");
            }
            string? destinationDirectoryPath = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectoryPath))
            {
                Directory.CreateDirectory(destinationDirectoryPath);
            }
            if (overwrite && File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
            File.Move(sourcePath, destinationPath);
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            OperationObserver?.Invoke("filesystem");
            if (!string.IsNullOrWhiteSpace(MoveDirectoryFailureSourcePath)
                && string.Equals(sourcePath, MoveDirectoryFailureSourcePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Synthetic directory move failure for batch behavior test.");
            }
            if (overwrite && Directory.Exists(destinationPath))
            {
                Directory.Delete(destinationPath, recursive: true);
            }
            string? destinationParentPath = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationParentPath))
            {
                Directory.CreateDirectory(destinationParentPath);
            }
            CopyDirectory(sourcePath, destinationPath);
            Directory.Delete(sourcePath, recursive: true);
        }

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            string? destinationDirectoryPath = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectoryPath))
            {
                Directory.CreateDirectory(destinationDirectoryPath);
            }
            File.Copy(sourcePath, destinationPath, overwrite);
        }

        public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            OperationObserver?.Invoke("filesystem");
            if (!string.IsNullOrWhiteSpace(MoveDirectoryFailureSourcePath)
                && string.Equals(sourcePath, MoveDirectoryFailureSourcePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Synthetic directory move failure for batch behavior test.");
            }
            CopyDirectoryTree(sourcePath, destinationPath, overwrite);
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
            if (!string.IsNullOrWhiteSpace(DeleteDirectoryFailurePath)
                && string.Equals(directoryPath, DeleteDirectoryFailurePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Synthetic directory delete failure for manual recovery test.");
            }
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
        }

        private static void CopyDirectory(string sourcePath, string destinationPath)
        {
            Directory.CreateDirectory(destinationPath);
            foreach (string directoryPath in Directory.GetDirectories(sourcePath, "*", System.IO.SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(directoryPath.Replace(sourcePath, destinationPath));
            }
            foreach (string filePath in Directory.GetFiles(sourcePath, "*", System.IO.SearchOption.AllDirectories))
            {
                string destinationFilePath = filePath.Replace(sourcePath, destinationPath);
                string? destinationDirectoryPath = Path.GetDirectoryName(destinationFilePath);
                if (!string.IsNullOrWhiteSpace(destinationDirectoryPath))
                {
                    Directory.CreateDirectory(destinationDirectoryPath);
                }
                File.Copy(filePath, destinationFilePath, overwrite: true);
            }
        }

        private static void CopyDirectoryTree(string sourcePath, string destinationPath, bool overwrite)
        {
            CopyDirectory(sourcePath, destinationPath);
            if (!overwrite)
            {
                return;
            }
        }
    }
}

internal sealed class RecordingFolderAutoRenameProgressWriter(
    Action<FolderAutoRenameProgressUpdate>? onUpdate = null) : IFolderAutoRenameProgressWriter
{
    public void TryWrite(FolderAutoRenameProgressUpdate update) => onUpdate?.Invoke(update);
}
