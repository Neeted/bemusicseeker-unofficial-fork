using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using Livet;
using Microsoft.VisualBasic.FileIO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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
                int baselineOwnedCollectionVersion = library.OwnedChartCollectionVersion;

                FileDbMutationReceipt receipt = library.RenameChartFolderWithReceipt(
                    sourceDirectoryPath,
                    "Renamed",
                    reportAtTerminal: reportAtTerminal);

                Assert.IsNotNull(receipt);
                Assert.IsFalse(receipt.DurableCommit);
                Assert.AreEqual(FileDbMutationTerminalState.Failed, receipt.TerminalState);
                Assert.AreEqual(baselineOwnedCollectionVersion, library.OwnedChartCollectionVersion);
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
                FileDbMutationReceipt receipt = library.RenameChartFolderWithReceipt(source, "destination", reportAtTerminal: reportAtTerminal);
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
    public void AutoRenameChartFolders_ProgressRunsAfterFilesystemMutation()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_AutoRenameDeferred_" + Guid.NewGuid().ToString("N"));
            string libraryRootPath = Path.Combine(tempRootPath, "LibraryRoot");
            string sourceDirectoryPath = Path.Combine(libraryRootPath, "Source");
            string chartPath = Path.Combine(sourceDirectoryPath, "chart.bms");
            Directory.CreateDirectory(sourceDirectoryPath);
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Deferred\r\n#ARTIST Artist");
            List<string> phases = [];
            TestBmsLibrary? library = null;
            bool filesystemObservedActiveLease = false;
            bool progressObservedReleasedLease = false;
            var fileMutationService = new TestFileMutationService
            {
                OperationObserver = phase =>
                {
                    phases.Add(phase);
                    using LibraryFileMutationLease? probe = library?.TryBeginLibraryFileMutation(
                        "auto_rename_filesystem_probe");
                    filesystemObservedActiveLease |= probe == null;
                }
            };
            try
            {
                library = new TestBmsLibrary(
                    songDbPath,
                    null,
                    null,
                    fileMutationService,
                    new RecordingDialogService())
                {
                    SearchTargets = [libraryRootPath]
                };
                var file = new TestableBmsFile { path = chartPath };
                file.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                file.SetTitle("Deferred");
                file.SetArtist("Artist");
                library.BMSFiles = [file];

                library.AutoRenameChartFolders(
                    [ChartFileProjection.FromBmsFile(file)],
                    progressReporter: (_, _, _) =>
                    {
                        phases.Add("progress");
                        using LibraryFileMutationLease probe = library.TryBeginLibraryFileMutation(
                            "auto_rename_progress_probe");
                        progressObservedReleasedLease |= probe != null;
                    });

                int filesystemIndex = phases.IndexOf("filesystem");
                int progressIndex = phases.IndexOf("progress");
                Assert.IsTrue(filesystemIndex >= 0, string.Join("|", phases));
                Assert.IsTrue(progressIndex > filesystemIndex, string.Join("|", phases));
                Assert.IsTrue(filesystemObservedActiveLease);
                Assert.IsTrue(progressObservedReleasedLease);
                Assert.AreEqual(Path.Combine(libraryRootPath, "[Artist] Deferred", "chart.bms"), file.path);
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
                var file = new TestableBmsFile
                {
                    path = chartPath
                };
                file.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                int bmsFilesChangedCount = 0;
                int folderChangedCount = 0;
                int pathChangedCount = 0;
                var bmsFilesPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var folderChanged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var pathChanged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                library.PropertyChanged += delegate (object? sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSLibrary.BMSFiles))
                    {
                        Interlocked.Increment(ref bmsFilesChangedCount);
                        bmsFilesPublished.TrySetResult(true);
                    }
                };
                library.BMSFiles = [file];
                await bmsFilesPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Interlocked.Exchange(ref bmsFilesChangedCount, 0);
                library.DuplicateChartGroups = [];
                int baselineOwnedCollectionVersion = library.OwnedChartCollectionVersion;
                int baselineParentFolderVersion = library.BMSParentFolderListCacheVersion;
                int baselineDuplicateInvalidationVersion = library.DuplicateChartGroupsInvalidationVersion;
                int ownedCollectionVersionChangedCount = 0;
                int parentFolderVersionChangedCount = 0;
                bool stateAvailableAtOwnedCollectionNotification = false;
                library.PropertyChanged += delegate (object? sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSLibrary.OwnedChartCollectionVersion))
                    {
                        Interlocked.Increment(ref ownedCollectionVersionChangedCount);
                        stateAvailableAtOwnedCollectionNotification = string.Equals(file.Folder, "Renamed", StringComparison.Ordinal)
                            && file.path.Contains(Path.Combine("Renamed", "chart.bms"), StringComparison.OrdinalIgnoreCase);
                    }
                    if (e.PropertyName == nameof(BMSLibrary.BMSParentFolderListCacheVersion))
                    {
                        Interlocked.Increment(ref parentFolderVersionChangedCount);
                    }
                };
                file.PropertyChanged += delegate (object? sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSFile.Folder))
                    {
                        Interlocked.Increment(ref folderChangedCount);
                        folderChanged.TrySetResult(true);
                    }
                    if (e.PropertyName == nameof(BMSFile.path))
                    {
                        Interlocked.Increment(ref pathChangedCount);
                        pathChanged.TrySetResult(true);
                    }
                };

                library.RenameChartFolder(sourceDirectoryPath, "Renamed");

                await Task.WhenAll(folderChanged.Task, pathChanged.Task);
                Assert.AreEqual(0, Volatile.Read(ref bmsFilesChangedCount));
                Assert.AreEqual(1, Volatile.Read(ref folderChangedCount));
                Assert.AreEqual(1, Volatile.Read(ref pathChangedCount));
                Assert.AreEqual("Renamed", file.Folder);
                Assert.IsTrue(file.path.Contains(Path.Combine("Renamed", "chart.bms")));
                Assert.AreEqual(baselineOwnedCollectionVersion + 1, library.OwnedChartCollectionVersion);
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
                var file = new TestableBmsFile { path = chartPath };
                file.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                library.BMSFiles = [file];

                int publicationCount = 0;
                bool publicationObservedFinalizedFilesystem = false;
                bool publicationObservedReleasedLease = false;
                library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
                {
                    if (args.PropertyName != nameof(BMSLibrary.OwnedChartCollectionVersion))
                    {
                        return;
                    }
                    Interlocked.Increment(ref publicationCount);
                    publicationObservedFinalizedFilesystem = !Directory.Exists(sourceDirectoryPath)
                        && Directory.Exists(destinationDirectoryPath)
                        && string.Equals(file.path, destinationChartPath, StringComparison.OrdinalIgnoreCase);
                    using LibraryFileMutationLease probe = library.TryBeginLibraryFileMutation(
                        "normal_rename_publication_probe");
                    publicationObservedReleasedLease = probe != null;
                };

                FileDbMutationReceipt receipt = library.RenameChartFolderWithReceipt(
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
                var file = new TestableBmsFile { path = sourceChartPath };
                file.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
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
                    BMSFiles = [file]
                };
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                    songDb.Execute(
                        "CREATE TRIGGER fail_lr2_folder_insert BEFORE INSERT ON folder WHEN NEW.path LIKE '%PackFinalizationFailure%' "
                        + "BEGIN SELECT RAISE(ABORT, 'forced durable finalization failure'); END;");
                }

                int ownedCollectionPublicationCount = 0;
                int normalRefreshPublicationCount = 0;
                library.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(BMSLibrary.OwnedChartCollectionVersion))
                    {
                        ownedCollectionPublicationCount++;
                    }
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        normalRefreshPublicationCount++;
                    }
                };

                FileDbMutationReceipt receipt = library.RenameChartFolderWithReceipt(
                    sourceDirectoryPath,
                    "PackFinalizationFailure");

                Assert.IsNotNull(receipt);
                Assert.IsTrue(receipt.DurableCommit);
                Assert.AreEqual(FileDbMutationTerminalState.DurableFinalizationFailed, receipt.TerminalState);
                Assert.IsNotNull(receipt.FinalizationFailure);
                Assert.AreSame(receipt.FinalizationFailure, receipt.Failure);
                Assert.IsFalse(receipt.HasCleanupFailure);
                Assert.IsFalse(receipt.RecoveryPaths.Any());
                Assert.IsFalse(Directory.Exists(sourceDirectoryPath));
                Assert.IsTrue(File.Exists(destinationChartPath));
                Assert.AreEqual(destinationChartPath, file.path);
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
    [DataRow(16, false)]
    [DataRow(128, false)]
    [DataRow(16, true)]
    [DataRow(128, true)]
    public void RenameIngress_CapturesOnlyLocalBmsRangeFacts(int backgroundChartCount, bool autoRename)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(
                Path.GetTempPath(),
                "BeMusicSeeker_LocalBmsScopeCapture_" + Guid.NewGuid().ToString("N"));
            string libraryRootPath = Path.Combine(tempRootPath, "LibraryRoot");
            string sourceDirectoryPath = Path.Combine(libraryRootPath, "TargetSource");
            string backgroundDirectoryPath = Path.Combine(libraryRootPath, "Background");
            string targetChartPath = Path.Combine(sourceDirectoryPath, "target.bms");
            string lr2RootPath = Path.Combine(tempRootPath, "LR2beta3");
            Directory.CreateDirectory(sourceDirectoryPath);
            Directory.CreateDirectory(backgroundDirectoryPath);
            File.WriteAllText(
                targetChartPath,
                "#PLAYER 1\r\n#TITLE Target Title\r\n#ARTIST Target Artist\r\n");
            try
            {
                var target = new TestableBmsFile { path = targetChartPath };
                target.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                target.SetTitle("Target Title");
                target.SetArtist("Target Artist");
                var files = new List<TestableBmsFile> { target };
                for (int index = 0; index < backgroundChartCount; index++)
                {
                    var background = new TestableBmsFile
                    {
                        path = Path.Combine(backgroundDirectoryPath, $"background-{index:D3}.bms")
                    };
                    background.SetHash(index.ToString("x32"));
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
                    BMSFiles = files
                };
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    foreach (TestableBmsFile file in files)
                    {
                        songDb.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                    }
                }

                var before =
                    library.WarmOwnedRealPathDirectoryView("R3局所BMS範囲捕捉前");
                if (autoRename)
                {
                    AutoRenameBatchResult result = library.AutoRenameChartFoldersWithResult(
                        [ChartFileProjection.FromBmsFile(target)]);
                    Assert.IsTrue(result.HasDurableCommit);
                }
                else
                {
                    FileDbMutationReceipt receipt = library.RenameChartFolderWithReceipt(
                        sourceDirectoryPath,
                        "Renamed");
                    Assert.IsNotNull(receipt);
                    Assert.IsTrue(receipt.DurableCommit);
                }

                var after =
                    library.WarmOwnedRealPathDirectoryView("R3局所BMS範囲捕捉後");
                int countQueryDelta = after.BmsCountQueryCount - before.BmsCountQueryCount;
                int rangeQueryDelta = after.BmsRangeQueryCount - before.BmsRangeQueryCount;
                int visitedReferenceDelta =
                    after.BmsRangeVisitedReferenceCount - before.BmsRangeVisitedReferenceCount;
                int returnedPathDelta = after.BmsRangeReturnedPathCount - before.BmsRangeReturnedPathCount;

                Assert.IsTrue(countQueryDelta > 0, "receipt捕捉ではBMS祖先件数queryを実行すること。");
                Assert.IsTrue(rangeQueryDelta > 0, "receipt捕捉ではBMS範囲queryを実行すること。");
                Assert.IsTrue(returnedPathDelta > 0, "receipt捕捉では対象BMSのexact pathを保持すること。");
                Assert.IsTrue(
                    visitedReferenceDelta <= 1,
                    $"対象1譜面の局所範囲queryが訪問したref数は1以下であること（実測{visitedReferenceDelta}、背景{backgroundChartCount}）。");

                string destinationDirectoryPath = autoRename
                    ? Path.Combine(libraryRootPath, "[Target Artist] Target Title")
                    : Path.Combine(libraryRootPath, "Renamed");
                Assert.AreEqual(Path.Combine(destinationDirectoryPath, "target.bms"), target.path);
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
            File.WriteAllText(firstChartPath, "#PLAYER 1");
            File.WriteAllText(secondChartPath, "#PLAYER 1");
            File.WriteAllText(Path.Combine(firstDirectoryPath, "first.wav"), "audio");
            try
            {
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
                {
                    SearchTargets = [libraryRootPath]
                };
                var firstFile = new TestableBmsFile
                {
                    path = firstChartPath
                };
                firstFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                firstFile.SetTitle("First Title");
                firstFile.SetArtist("First Artist");
                var secondFile = new TestableBmsFile
                {
                    path = secondChartPath
                };
                secondFile.SetHash("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
                secondFile.SetTitle("Second Title");
                secondFile.SetArtist("Second Artist");
                library.BMSFiles = [firstFile, secondFile];
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

                library.AutoRenameChartFolders([
                    ChartFileProjection.FromBmsFile(firstFile),
                    ChartFileProjection.FromBmsFile(secondFile)
                ], progressReporter: (total, processed, path) => progress.Add((total, processed, path)));
                NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

                Assert.AreEqual(1, Volatile.Read(ref refreshCount));
                Assert.IsFalse(batch.NotifiesStorageRows);
                CollectionAssert.AreEqual(new[] { 2 }, progress.Select(item => item.Processed).ToArray());
                Assert.IsTrue(progress.All(item => item.Total == 2));
                CollectionAssert.AreEqual(new[] { secondDirectoryPath }, progress.Select(item => item.Path).ToArray());
                Assert.AreEqual(Path.Combine(libraryRootPath, "[First Artist] First Title", "first.bms"), firstFile.path);
                Assert.AreEqual(Path.Combine(libraryRootPath, "[Second Artist] Second Title", "second.bms"), secondFile.path);
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
                var pendingPackage = ChartPackageTestExtensions.CreatePackage(
                    [BMSFile.CreateBMSFileFromFile(pendingChartPath)]);
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
                ChartPackage pendingBmsonPackage = ChartPackage.FromChartEntries(
                [
                    PackageChartEntry.FromChart(
                        ChartFileProjection.FromBmsonSong(BmsonSongParser.Parse(pendingBmsonPath)))
                ]);
                pendingBmsonPackage.path = pendingBmsonPath;
                pendingBmsonPackage.delete_parent = true;
                library.ChartPackagesPending = CreatePackageCollection([pendingPackage, pendingBmsonPackage]);

                library.SearchEstimatedInstallationDirectory(pendingBmsonPackage);

                PackageChartEntry pendingBmsonEntry = pendingBmsonPackage.ChartEntries.Single();
                Assert.IsNull(pendingBmsonEntry.GetBmsOwnerForTest());
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
                var firstFile = new TestableBmsFile
                {
                    path = firstChartPath
                };
                firstFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                firstFile.SetTitle("Same Title");
                firstFile.SetArtist("Same Artist");
                var secondFile = new TestableBmsFile
                {
                    path = secondChartPath
                };
                secondFile.SetHash("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
                secondFile.SetTitle("Same Title");
                secondFile.SetArtist("Same Artist");
                library.BMSFiles = [firstFile, secondFile];

                library.AutoRenameChartFolders([
                    ChartFileProjection.FromBmsFile(firstFile),
                    ChartFileProjection.FromBmsFile(secondFile)
                ]);

                string firstDestinationPath = Path.Combine(libraryRootPath, "[Same Artist] Same Title");
                string secondDestinationPath = Path.Combine(libraryRootPath, "[Same Artist] Same Title (2)");
                Assert.AreEqual(Path.Combine(firstDestinationPath, "first.bms"), firstFile.path);
                Assert.AreEqual(Path.Combine(secondDestinationPath, "second.bms"), secondFile.path);
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
                var file = new TestableBmsFile
                {
                    path = chartPath
                };
                file.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                file.SetTitle("T");
                file.SetArtist("A");
                library.BMSFiles = [file];

                library.AutoRenameChartFolders([ChartFileProjection.FromBmsFile(file)]);

                string destinationDirectoryPath = Path.Combine(libraryRootPath, "[A] T");
                Assert.AreEqual(Path.Combine(destinationDirectoryPath, chartFileName), file.path);
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
                var file = new TestableBmsFile { path = sourceChartPath };
                file.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                file.SetTitle("PackFinalizationFailure");
                file.SetArtist("Artist");
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
                    BMSFiles = [file]
                };
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                    songDb.Execute(
                        "CREATE TRIGGER fail_lr2_folder_insert BEFORE INSERT ON folder WHEN NEW.path LIKE '%PackFinalizationFailure%' "
                        + "BEGIN SELECT RAISE(ABORT, 'forced durable finalization failure'); END;");
                }

                int ownedCollectionPublicationCount = 0;
                int normalRefreshPublicationCount = 0;
                library.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(BMSLibrary.OwnedChartCollectionVersion))
                    {
                        ownedCollectionPublicationCount++;
                    }
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        normalRefreshPublicationCount++;
                    }
                };

                AutoRenameBatchResult result = library.AutoRenameChartFoldersWithResult(
                    [ChartFileProjection.FromBmsFile(file)]);

                Assert.IsNotNull(result);
                Assert.IsTrue(result.HasDurableCommit);
                Assert.IsTrue(result.HasDurableFinalizationFailure);
                Assert.IsNotNull(result.PrimaryFailure);
                Assert.AreEqual(1, result.AppliedPlanCount);
                FileDbMutationReceipt receipt = result.MutationReceipt.Receipts.Single();
                // The LR2 normal-folder sync is the auto command's single
                // batch finalizer, after the individual folder mutation has
                // already completed durably.  Keep that failure at the batch
                // boundary instead of mislabeling the completed item receipt.
                Assert.AreEqual(FileDbMutationTerminalState.Completed, receipt.TerminalState);
                Assert.AreSame(result.MutationReceipt.FinalizationFailure, result.PrimaryFailure.SourceException);
                Assert.IsTrue(result.MutationReceipt.HasDurableFinalizationFailure);
                Assert.IsTrue(Directory.Exists(destinationDirectoryPath));
                Assert.IsFalse(Directory.Exists(sourceDirectoryPath));
                Assert.IsTrue(File.Exists(destinationChartPath));
                Assert.AreEqual(destinationChartPath, file.path);
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
    public void ApplyAutoRenamePlans_BatchesSuccessfulMovesWhenOnePlanFails(bool reportAtTerminal)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_AutoRenamePartialBatch_" + Guid.NewGuid().ToString("N"));
            string libraryRootPath = Path.Combine(tempRootPath, "LibraryRoot");
            string firstDirectoryPath = Path.Combine(libraryRootPath, "FirstSource");
            string firstExistingDestinationPath = Path.Combine(libraryRootPath, "FirstExisting");
            string secondDirectoryPath = Path.Combine(libraryRootPath, "SecondSource");
            string secondDestinationPath = Path.Combine(libraryRootPath, "[Second Artist] Second Renamed");
            string firstChartPath = Path.Combine(firstDirectoryPath, "first.bms");
            string secondChartPath = Path.Combine(secondDirectoryPath, "second.bms");
            Directory.CreateDirectory(firstDirectoryPath);
            Directory.CreateDirectory(firstExistingDestinationPath);
            Directory.CreateDirectory(secondDirectoryPath);
            File.WriteAllText(firstChartPath, "#PLAYER 1");
            File.WriteAllText(secondChartPath, "#PLAYER 1");
            try
            {
                var fileMutationService = new TestFileMutationService
                {
                    MoveDirectoryFailureSourcePath = firstDirectoryPath
                };
                var dialogs = new FileDbReportRecordingDialogs();
                var library = new TestBmsLibrary(songDbPath, null, null, fileMutationService, dialogs)
                {
                    SearchTargets = [libraryRootPath]
                };
                var firstFile = new TestableBmsFile
                {
                    path = firstChartPath
                };
                firstFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                firstFile.SetTitle("First Existing");
                firstFile.SetArtist("First Artist");
                var secondFile = new TestableBmsFile
                {
                    path = secondChartPath
                };
                secondFile.SetHash("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
                secondFile.SetTitle("Second Renamed");
                secondFile.SetArtist("Second Artist");
                library.BMSFiles = [firstFile, secondFile];
                int refreshCount = 0;
                library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
                {
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        Interlocked.Increment(ref refreshCount);
                    }
                };
                int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;

                AutoRenameBatchResult receiptResult = library.AutoRenameChartFoldersWithResult([
                    ChartFileProjection.FromBmsFile(firstFile),
                    ChartFileProjection.FromBmsFile(secondFile)
                ], reportAtTerminal: reportAtTerminal);
                Assert.AreEqual(reportAtTerminal ? 0 : 1, dialogs.ModelMessages);
                Assert.IsTrue(receiptResult.MutationReceipt.Receipts.Any(receipt => !receipt.DurableCommit));
                Assert.IsTrue(receiptResult.HasDurableCommit);
                NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

                Assert.AreEqual(1, Volatile.Read(ref refreshCount));
                Assert.IsFalse(batch.NotifiesStorageRows);
                Assert.AreEqual(firstChartPath, firstFile.path);
                Assert.AreEqual(Path.Combine(secondDestinationPath, "second.bms"), secondFile.path);
                Assert.IsTrue(Directory.Exists(firstDirectoryPath));
                Assert.IsTrue(Directory.Exists(firstExistingDestinationPath));
                Assert.IsFalse(Directory.Exists(secondDirectoryPath));
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
                var firstFile = new TestableBmsFile { path = firstChartPath };
                firstFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                firstFile.SetTitle("First Title");
                firstFile.SetArtist("First Artist");
                var secondFile = new TestableBmsFile { path = secondChartPath };
                secondFile.SetHash("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
                secondFile.SetTitle("Second Title");
                secondFile.SetArtist("Second Artist");
                library.BMSFiles = [firstFile, secondFile];
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
                    if (args.PropertyName != nameof(BMSLibrary.OwnedChartCollectionVersion))
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

                AutoRenameBatchResult result = library.AutoRenameChartFoldersWithResult([
                    ChartFileProjection.FromBmsFile(firstFile),
                    ChartFileProjection.FromBmsFile(secondFile)
                ]);

                Assert.IsNotNull(result);
                Assert.IsTrue(result.HasActionablePlan);
                Assert.AreEqual(2, result.AppliedPlanCount);
                Assert.IsTrue(result.HasDurableCommit);
                Assert.IsFalse(result.ManualRecoveryRequired);
                Assert.IsNull(result.PrimaryFailure);
                Assert.AreEqual(2, result.MutationReceipt.Receipts.Count);
                Assert.IsTrue(result.MutationReceipt.Receipts.All(receipt =>
                    receipt.DurableCommit
                    && receipt.TerminalState == FileDbMutationTerminalState.Completed));
                Assert.AreEqual(2, publicationCount);
                Assert.AreEqual(2, postReleasePublicationCount);
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
    public void AutoRenameChartFolders_ContinuesWhenProgressReporterThrows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_AutoRenameProgressFailure_" + Guid.NewGuid().ToString("N"));
            string libraryRootPath = Path.Combine(tempRootPath, "LibraryRoot");
            string sourceDirectoryPath = Path.Combine(libraryRootPath, "Source");
            string chartPath = Path.Combine(sourceDirectoryPath, "chart.bms");
            Directory.CreateDirectory(sourceDirectoryPath);
            File.WriteAllText(chartPath, "#PLAYER 1");
            try
            {
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
                {
                    SearchTargets = [libraryRootPath]
                };
                var file = new TestableBmsFile
                {
                    path = chartPath
                };
                file.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                file.SetTitle("Title");
                file.SetArtist("Artist");
                library.BMSFiles = [file];

                library.AutoRenameChartFolders(
                    [ChartFileProjection.FromBmsFile(file)],
                    progressReporter: delegate { throw new InvalidOperationException("progress failure"); });

                Assert.AreEqual(Path.Combine(libraryRootPath, "[Artist] Title", "chart.bms"), file.path);
                Assert.IsFalse(Directory.Exists(sourceDirectoryPath));
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
                var file = new TestableBmsFile
                {
                    path = chartPath
                };
                file.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                int bmsFilesChangedCount = 0;
                int normalLibraryRefreshCount = 0;
                var bmsFilesPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                library.PropertyChanged += delegate (object? sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSLibrary.BMSFiles))
                    {
                        Interlocked.Increment(ref bmsFilesChangedCount);
                        bmsFilesPublished.TrySetResult(true);
                    }
                    if (e.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        Interlocked.Increment(ref normalLibraryRefreshCount);
                    }
                };
                library.BMSFiles = [file];
                await bmsFilesPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Interlocked.Exchange(ref bmsFilesChangedCount, 0);
                Interlocked.Exchange(ref normalLibraryRefreshCount, 0);
                int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;

                library.MoveLibraryRootFolder([LibraryChartRef.FromChartFile(ChartFileProjection.FromBmsFile(file))], destinationParentPath);
                NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

                Assert.AreEqual(0, Volatile.Read(ref bmsFilesChangedCount));
                Assert.IsTrue(Volatile.Read(ref normalLibraryRefreshCount) > 0);
                Assert.IsTrue(batch.NotifiesStorageRows);
                Assert.IsTrue(batch.NotifiesBmsFiles);
                Assert.IsFalse(batch.NotifiesBmsonSongs);
                Assert.IsTrue(file.path.Contains(Path.Combine("DestinationParent", "SourceRoot", "chart.bms")));
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
                var song = new LR2SongDBExtended.bmson_song
                {
                    path = chartPath,
                    folder = sourceDirectoryPath,
                    md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    title = "Chart"
                };
                int bmsFilesChangedCount = 0;
                int bmsonSongsChangedCount = 0;
                var bmsonSongsPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                library.PropertyChanged += delegate (object? sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSLibrary.BMSFiles))
                    {
                        Interlocked.Increment(ref bmsFilesChangedCount);
                    }
                    if (e.PropertyName == nameof(BMSLibrary.BmsonSongs))
                    {
                        Interlocked.Increment(ref bmsonSongsChangedCount);
                        bmsonSongsPublished.TrySetResult(true);
                    }
                };
                library.BmsonSongs = [song];
                await bmsonSongsPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Interlocked.Exchange(ref bmsFilesChangedCount, 0);
                Interlocked.Exchange(ref bmsonSongsChangedCount, 0);

                library.RenameChartFolder(sourceDirectoryPath, "Renamed");
                LibraryChartRow row = LibraryChartRow.FromBmsonSong(song);

                Assert.AreEqual(0, Volatile.Read(ref bmsFilesChangedCount));
                Assert.AreEqual(0, Volatile.Read(ref bmsonSongsChangedCount));
                Assert.AreEqual("Renamed", row.Folder);
                Assert.IsTrue(song.path.Contains(Path.Combine("Renamed", "chart.bmson")));
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
    /// folder move の DB precommit failure に対する compensation failure は、recovery tree を残して後続 folder を開始しないことを検証します。
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MoveLibraryRootFolder_StopsAfterManualRecoveryRequired(bool reportAtTerminal)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_FolderManualRecovery_" + Guid.NewGuid().ToString("N"));
            string sourceRootPath = Path.Combine(tempRootPath, "SourceRoot");
            string firstSourceDirectoryPath = Path.Combine(sourceRootPath, "First");
            string secondSourceDirectoryPath = Path.Combine(sourceRootPath, "Second");
            string destinationRootPath = Path.Combine(tempRootPath, "DestinationRoot");
            string firstChartPath = Path.Combine(firstSourceDirectoryPath, "first.bms");
            string secondChartPath = Path.Combine(secondSourceDirectoryPath, "second.bms");
            Directory.CreateDirectory(firstSourceDirectoryPath);
            Directory.CreateDirectory(secondSourceDirectoryPath);
            Directory.CreateDirectory(destinationRootPath);
            File.WriteAllText(firstChartPath, "#PLAYER 1\r\n#TITLE First");
            File.WriteAllText(secondChartPath, "#PLAYER 1\r\n#TITLE Second");
            try
            {
                var fileMutationService = new TestFileMutationService
                {
                    DeleteDirectoryFailurePath = Path.Combine(destinationRootPath, "First")
                };
                var dialogs = new RecordingDialogService();
                var library = new TestBmsLibrary(
                    songDbPath,
                    null,
                    null,
                    fileMutationService,
                    dialogs);
                var firstFile = new TestableBmsFile { path = firstChartPath };
                firstFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                var secondFile = new TestableBmsFile { path = secondChartPath };
                secondFile.SetHash("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
                library.BMSFiles = [firstFile, secondFile];
                int baselineOwnedCollectionVersion = library.OwnedChartCollectionVersion;
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(firstFile.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(secondFile.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                    string firstDestinationChartPath = Path.Combine(destinationRootPath, "First", "first.bms");
                    string escapedPath = firstDestinationChartPath.Replace("'", "''");
                    songDb.Execute(
                        "CREATE TRIGGER fail_folder_move BEFORE INSERT ON song WHEN NEW.path = '"
                        + escapedPath
                        + "' BEGIN SELECT RAISE(ABORT, 'forced folder move failure'); END;");
                }

                FileDbMutationBatchReceipt receipt = library.MoveLibraryRootFolderWithReceipt(
                    [
                        LibraryChartRef.FromChartFile(ChartFileProjection.FromBmsFile(firstFile)),
                        LibraryChartRef.FromChartFile(ChartFileProjection.FromBmsFile(secondFile))
                    ],
                    destinationRootPath,
                    reportAtTerminal: reportAtTerminal);

                Assert.IsTrue(receipt.ManualRecoveryRequired);
                Assert.AreEqual(1, receipt.Receipts.Count, "The unprocessed second folder has no successful receipt.");
                Assert.AreEqual(reportAtTerminal ? 0 : 1, dialogs.CallCount);

                string firstDestinationDirectoryPath = Path.Combine(destinationRootPath, "First");
                Assert.IsTrue(Directory.Exists(firstSourceDirectoryPath));
                Assert.IsTrue(Directory.Exists(firstDestinationDirectoryPath));
                Assert.IsTrue(Directory.Exists(secondSourceDirectoryPath));
                Assert.IsFalse(Directory.Exists(Path.Combine(destinationRootPath, "Second")));
                Assert.AreEqual(firstChartPath, firstFile.path);
                Assert.AreEqual(secondChartPath, secondFile.path);
                Assert.AreEqual(baselineOwnedCollectionVersion, library.OwnedChartCollectionVersion);
                using var verifySongDb = new LR2SongDBExtended(songDbPath);
                Assert.IsTrue(verifySongDb.Table<LR2SongDB.song>().Any(row => row.path == firstChartPath));
                Assert.IsTrue(verifySongDb.Table<LR2SongDB.song>().Any(row => row.path == secondChartPath));
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
                var song = new LR2SongDBExtended.bmson_song
                {
                    path = chartPath,
                    folder = sourceRootPath,
                    md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    title = "Chart"
                };
                int bmsFilesChangedCount = 0;
                int bmsonSongsChangedCount = 0;
                int normalLibraryRefreshCount = 0;
                var bmsonSongsPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                library.PropertyChanged += delegate (object? sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSLibrary.BMSFiles))
                    {
                        Interlocked.Increment(ref bmsFilesChangedCount);
                    }
                    if (e.PropertyName == nameof(BMSLibrary.BmsonSongs))
                    {
                        Interlocked.Increment(ref bmsonSongsChangedCount);
                        bmsonSongsPublished.TrySetResult(true);
                    }
                    if (e.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        Interlocked.Increment(ref normalLibraryRefreshCount);
                    }
                };
                library.BmsonSongs = [song];
                await bmsonSongsPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Interlocked.Exchange(ref bmsFilesChangedCount, 0);
                Interlocked.Exchange(ref bmsonSongsChangedCount, 0);
                Interlocked.Exchange(ref normalLibraryRefreshCount, 0);
                int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;

                library.MoveLibraryRootFolder([LibraryChartRef.FromChartFile(ChartFileProjection.FromBmsonSong(song))], destinationParentPath);
                NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

                Assert.AreEqual(0, Volatile.Read(ref bmsFilesChangedCount));
                Assert.AreEqual(0, Volatile.Read(ref bmsonSongsChangedCount));
                Assert.IsTrue(Volatile.Read(ref normalLibraryRefreshCount) > 0);
                Assert.IsTrue(batch.NotifiesStorageRows);
                Assert.IsFalse(batch.NotifiesBmsFiles);
                Assert.IsTrue(batch.NotifiesBmsonSongs);
                Assert.IsTrue(song.path.Contains(Path.Combine("DestinationParent", "SourceRoot", "chart.bmson")));
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
                var song = new LR2SongDBExtended.bmson_song
                {
                    path = sourceChartPath,
                    folder = sourceDirectoryPath,
                    title = "Repair Bmson",
                    md5 = "0123456789abcdef0123456789abcdef",
                    sha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                };
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    songDb.InsertOrReplace(song, typeof(LR2SongDBExtended.bmson_song));
                }
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                library.BmsonSongs = [song];
                ChartFile repairTarget = ChartFileProjection.WithPackageState(
                    ChartFileProjection.FromBmsonSong(song),
                    destinationDirectoryPath,
                    string.Empty,
                    string.Empty,
                    []);

                library.FixInstallationDirectoryCharts([repairTarget]);

                Assert.AreEqual(destinationChartPath, song.path);
                Assert.AreEqual(destinationDirectoryPath, song.folder);
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
    public void FixInstallationDirectoryCharts_DatabaseCommitFailureRetainsSource(bool bmson)
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
                BMSFile? bmsFile = null;
                LR2SongDBExtended.bmson_song? bmsonSong = null;
                ChartFile chart;
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    if (bmson)
                    {
                        bmsonSong = new LR2SongDBExtended.bmson_song
                        {
                            path = sourceChartPath,
                            folder = sourceDirectoryPath,
                            title = "Repair DB failure",
                            md5 = "abcdefabcdefabcdefabcdefabcdefab",
                            sha256 = "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd"
                        };
                        songDb.InsertOrReplace(bmsonSong, typeof(LR2SongDBExtended.bmson_song));
                        chart = ChartFileProjection.FromBmsonSong(bmsonSong);
                    }
                    else
                    {
                        bmsFile = BMSFile.CreateBMSFileFromFile(sourceChartPath);
                        songDb.InsertOrReplace(bmsFile.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                        chart = ChartFileProjection.FromBmsFile(bmsFile);
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
                    BMSFiles = bmsFile == null ? [] : [bmsFile],
                    BmsonSongs = bmsonSong == null ? [] : [bmsonSong]
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
                Assert.IsNotNull(result.MutationReceipt);
                FileDbMutationReceipt receipt = result.MutationReceipt.Receipts.Single();
                Assert.IsFalse(receipt.DurableCommit);
                Assert.IsTrue(receipt.CompensationAttemptCount <= 1);
                Assert.IsTrue(File.Exists(sourceChartPath));
                Assert.IsFalse(File.Exists(destinationChartPath));
                Assert.AreEqual(sourceChartPath, bmsonSong?.path ?? bmsFile!.path);
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
                BMSFile? bmsFile = null;
                LR2SongDBExtended.bmson_song? bmsonSong = null;
                ChartFile chart;
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    if (bmson)
                    {
                        bmsonSong = new LR2SongDBExtended.bmson_song
                        {
                            path = sourceChartPath,
                            folder = sourceDirectoryPath,
                            title = "Repair collision",
                            md5 = "abcdefabcdefabcdefabcdefabcdefab",
                            sha256 = "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd"
                        };
                        songDb.InsertOrReplace(bmsonSong, typeof(LR2SongDBExtended.bmson_song));
                        chart = ChartFileProjection.FromBmsonSong(bmsonSong);
                    }
                    else
                    {
                        bmsFile = BMSFile.CreateBMSFileFromFile(sourceChartPath);
                        songDb.InsertOrReplace(bmsFile.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                        chart = ChartFileProjection.FromBmsFile(bmsFile);
                    }
                }

                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
                {
                    BMSFiles = bmsFile == null ? [] : [bmsFile],
                    BmsonSongs = bmsonSong == null ? [] : [bmsonSong]
                };
                ChartFile repairTarget = ChartFileProjection.WithPackageState(
                    chart,
                    destinationDirectoryPath,
                    string.Empty,
                    string.Empty,
                    []);

                LibraryFixInstallationResult result = library.FixInstallationDirectoryCharts([repairTarget]);

                Assert.IsNotNull(result);
                Assert.IsNull(result.Failure);
                FileDbMutationReceipt receipt = result.MutationReceipt.Receipts.Single();
                Assert.IsTrue(receipt.DurableCommit);
                string actualDestinationPath = receipt.DestinationPaths.Single();
                Assert.AreNotEqual(collisionPath, actualDestinationPath, ignoreCase: true);
                Assert.IsTrue(File.Exists(actualDestinationPath));
                Assert.IsTrue(File.Exists(sourceChartPath) == false);
                Assert.IsTrue(collisionBytes.SequenceEqual(File.ReadAllBytes(collisionPath)));
                Assert.AreEqual(actualDestinationPath, bmsonSong?.path ?? bmsFile!.path);
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
    /// R1: repair must recompute missing-resource health at the new location,
    /// persist it, and publish only after releasing its existing reservation.
    /// A maintenance DB failure remains an error after the path move succeeds.
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
            BMSFile? bmsFile = bmson ? null : BMSFile.CreateBMSFileFromFile(sourcePath);
            LR2SongDBExtended.bmson_song? bmsonSong = bmson ? BmsonSongParser.Parse(sourcePath) : null;
            ChartFile chart = bmson ? ChartFileProjection.FromBmsonSong(bmsonSong!) : ChartFileProjection.FromBmsFile(bmsFile!);
            BMSFileMaintenanceInfo initialInfo = BmsLibraryMaintenanceService.BuildResourceHealthMaintenanceInfo(chart);
            Assert.AreEqual(1, initialInfo.wav_files_defined);
            Assert.AreEqual(0, initialInfo.wav_files_existing);
            if (bmson)
                bmsonSong!.MaintenanceInfo = initialInfo;
            else
                bmsFile!.SetMaintenanceInfo(initialInfo, suppressPropertyChanged: true);
            chart = bmson ? ChartFileProjection.FromBmsonSong(bmsonSong!) : ChartFileProjection.FromBmsFile(bmsFile!);
            Assert.IsTrue(new BmsLibraryMaintenanceService().BuildResourceHealthWarnings(chart)
                .Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                BmsLibraryDbGateway.EnsureBmsonSchema(db);
                if (bmson)
                    db.InsertOrReplace(bmsonSong!, typeof(LR2SongDBExtended.bmson_song));
                else
                    db.InsertOrReplace(bmsFile!.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                db.InsertOrReplace(initialInfo, typeof(LR2SongDBExtended.maintenance));
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
                BMSFiles = bmsFile == null ? [] : [bmsFile],
                BmsonSongs = bmsonSong == null ? [] : [bmsonSong]
            };
            ChartFile repairTarget = ChartFileProjection.WithPackageState(chart, destination, string.Empty, string.Empty, []);
            int refreshCount = 0;
            bool notifiedWithLeaseHeld = false;
            bool notifiedWithCurrentHealth = false;
            library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    return;
                refreshCount++;
                using LibraryFileMutationLease probe = library.TryBeginLibraryFileMutation("repair_health_notification_probe");
                notifiedWithLeaseHeld |= probe == null;
                notifiedWithCurrentHealth |= (bmsonSong?.MaintenanceInfo ?? bmsFile?.TryGetMaintenanceInfoWithoutCreating())?.wav_files_existing == 1;
            };

            Exception? failure = null;
            try
            {
                failure = library.FixInstallationDirectoryCharts([repairTarget])?.Failure;
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            Assert.AreEqual(destinationPath, bmsonSong?.path ?? bmsFile!.path);
            Assert.IsFalse(File.Exists(sourcePath));
            Assert.IsTrue(File.Exists(destinationPath));
            Assert.IsTrue(refreshCount > 0);
            Assert.IsFalse(notifiedWithLeaseHeld);
            using LibraryFileMutationLease afterRepair = library.TryBeginLibraryFileMutation("repair_health_completion_probe");
            Assert.IsNotNull(afterRepair);
            if (failMaintenance)
            {
                Assert.IsNotNull(failure);
                Exception completedFailure = failure!;
                StringAssert.Contains(completedFailure.ToString(), "repair-health-write-fault");
            }
            else
            {
                Assert.IsNull(failure);
                Assert.IsTrue(notifiedWithCurrentHealth);
                BMSFileMaintenanceInfo updatedInfo = bmsonSong?.MaintenanceInfo ?? bmsFile!.maintenanceInfo;
                Assert.AreEqual(1, updatedInfo.wav_files_defined);
                Assert.AreEqual(1, updatedInfo.wav_files_existing);
                Assert.AreEqual(destinationPath, updatedInfo.path);
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
                BMSFile file = BMSFile.CreateBMSFileFromFile(sourceChartPath);
                string hash = file.hash;
                var existing = new TestableBmsFile
                {
                    path = sourceChartPath,
                    adddate = 12345,
                    tag = "external-user-tag"
                };
                existing.SetHash(hash);
                existing.SetFavorite(7);
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(existing, typeof(LR2SongDB.song));
                }

                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                library.BMSFiles = [file];
                ChartFile repairTarget = ChartFileProjection.WithPackageState(
                    ChartFileProjection.FromBmsFile(file),
                    destinationDirectoryPath,
                    string.Empty,
                    string.Empty,
                    []);

                library.FixInstallationDirectoryCharts([repairTarget]);

                using var verify = new LR2SongDBExtended(songDbPath);
                Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", sourceChartPath));
                LR2SongDB.song row = verify.Table<LR2SongDB.song>().Single(candidate => candidate.path == destinationChartPath);
                Assert.AreEqual(hash, row.hash);
                Assert.AreEqual("repaired", row.title);
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
    public void ApplyLibraryMutationDelta_BmsInstallDestinationUsesModelOverlayWithoutMutatingOwner()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            var file = new TestableBmsFile
            {
                path = @"C:\Library\chart.bms"
            };
            file.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            library.BMSFiles = [file];
            int baselineNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int refreshNotificationsChanged = 0;
            library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    refreshNotificationsChanged++;
                }
            };

            var delta = new LibraryMutationDelta();
            delta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
            {
                Chart = ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false),
                NewInstallDestination = @"C:\New"
            });

            InvokeApplyLibraryMutationDelta(library, delta);

            NormalLibraryRefreshNotificationBatch notificationBatch = library.GetNormalLibraryRefreshNotificationsAfter(baselineNotificationVersion);
            ChartFile changedChart = notificationBatch.InstallDestinationChangedCharts.Single();
            Assert.AreEqual(@"C:\New", changedChart.InstallDestination);
            Assert.IsFalse(notificationBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
            Assert.IsTrue(notificationBatch.HasEffect(LibraryChartRefreshEffects.InstallDestinationOverlayChanged));
            Assert.AreEqual(1, refreshNotificationsChanged);
            ChartFile installedChart = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshotWithInstallDestinationOverlay(library).Single();
            Assert.AreEqual(@"C:\New", installedChart.InstallDestination);
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
                var file = new TestableBmsFile
                {
                    path = chartPath
                };
                file.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                library.BMSFiles = [file];
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                    string escapedPath = chartPath.Replace("'", "''");
                    songDb.Execute(
                        "CREATE TRIGGER fail_library_delta_remove BEFORE DELETE ON song WHEN OLD.path = '"
                        + escapedPath
                        + "' BEGIN SELECT RAISE(ABORT, 'forced library mutation failure'); END;");
                }

                int notificationVersion = library.NormalLibraryRefreshNotificationVersion;
                int ownedCollectionVersion = library.OwnedChartCollectionVersion;
                ResourceHealthIndexSnapshot resourceHealthSnapshot = library.GetResourceHealthIndexSnapshotForView("failure_baseline");
                LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                    [LibraryChartRef.FromBmsFile(file)],
                    sendToRecycleBin: false,
                    approvedWholeFolderDeletePaths: []);

                Assert.AreEqual(notificationVersion, library.NormalLibraryRefreshNotificationVersion);
                Assert.AreEqual(ownedCollectionVersion, library.OwnedChartCollectionVersion);
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
                Assert.AreEqual(1, library.BMSFiles.Count);
                Assert.AreSame(file, library.BMSFiles.Single());
                using var verifySongDb = new LR2SongDBExtended(songDbPath);
                Assert.IsTrue(verifySongDb.Table<BMSFile>().Any(row => row.path == chartPath));
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
            var file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            library.BMSFiles = [file];
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
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
                rowStillExistsWhenNotificationWasPublished = notificationSongDb.Table<BMSFile>().Any(row => row.path == chartPath);
            };
            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                [LibraryChartRef.FromBmsFile(file)],
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: [chartDirectory]);

            Assert.IsFalse(outcome.HasError);
            Assert.AreEqual(0, library.BMSFiles.Count);
            NormalLibraryRefreshNotificationBatch notificationBatch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            Assert.IsTrue(notificationBatch.NotifiesStorageRows);
            Assert.IsFalse(rowStillExistsWhenNotificationWasPublished);
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            Assert.IsFalse(verifySongDb.Table<BMSFile>().Any(row => row.path == chartPath));
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
            var file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            library.BMSFiles = [file];
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
            }

            bool notificationAttempted = false;
            library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(BMSLibrary.OwnedChartCollectionVersion))
                {
                    notificationAttempted = true;
                    throw new InvalidOperationException("public notification failure");
                }
            };
            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                [LibraryChartRef.FromBmsFile(file)],
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: [chartDirectory]);

            Assert.IsTrue(notificationAttempted);
            Assert.IsFalse(outcome.HasError);
            Assert.AreEqual(0, library.BMSFiles.Count);
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            Assert.IsFalse(verifySongDb.Table<BMSFile>().Any(row => row.path == chartPath));
        });
    }

    [TestMethod]
    public void NormalLibraryRefreshNotifications_ExternalReplacementPublishesResetBarrier()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            var originalFile = new TestableBmsFile
            {
                path = @"C:\Library\chart.bms"
            };
            originalFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            library.BMSFiles = [originalFile];
            int baselineNotificationVersion = library.NormalLibraryRefreshNotificationVersion;

            var delta = new LibraryMutationDelta();
            delta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
            {
                Chart = ChartFileProjection.FromBmsFile(originalFile, includeWarningSnapshot: false),
                NewInstallDestination = @"C:\Overlay"
            });
            InvokeApplyLibraryMutationDelta(library, delta);

            var replacementFile = new TestableBmsFile
            {
                path = @"C:\Library\replacement.bms"
            };
            replacementFile.SetHash("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            library.BMSFiles = [replacementFile];

            NormalLibraryRefreshNotificationBatch notificationBatch = library.GetNormalLibraryRefreshNotificationsAfter(baselineNotificationVersion);
            Assert.IsTrue(notificationBatch.ResetsPriorNotifications);
            Assert.IsTrue(notificationBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
            Assert.IsTrue(notificationBatch.HasEffect(LibraryChartRefreshEffects.InstallDestinationOverlayChanged));
            Assert.AreEqual(0, notificationBatch.InstallDestinationChangedCharts.Count);
        });
    }

    [TestMethod]
    public void ApplyInstalledChartStorageTargets_DoesNotResetPriorOverlayNotification()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            var originalFile = new TestableBmsFile
            {
                path = @"C:\Library\chart.bms"
            };
            originalFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            library.BMSFiles = [originalFile];
            int baselineNotificationVersion = library.NormalLibraryRefreshNotificationVersion;

            var delta = new LibraryMutationDelta();
            delta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
            {
                Chart = ChartFileProjection.FromBmsFile(originalFile, includeWarningSnapshot: false),
                NewInstallDestination = @"C:\Overlay"
            });
            InvokeApplyLibraryMutationDelta(library, delta);

            var addedFile = new TestableBmsFile
            {
                path = @"C:\Library\added.bms"
            };
            addedFile.SetHash("cccccccccccccccccccccccccccccccc");
            InvokeApplyInstalledChartStorageTargets(
                library,
                ChartStorageTargetSet.FromRows([addedFile], []));

            NormalLibraryRefreshNotificationBatch notificationBatch = library.GetNormalLibraryRefreshNotificationsAfter(baselineNotificationVersion);
            Assert.IsFalse(notificationBatch.ResetsPriorNotifications);
            Assert.IsTrue(notificationBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
            Assert.IsTrue(notificationBatch.HasEffect(LibraryChartRefreshEffects.InstallDestinationOverlayChanged));
            ChartFile changedChart = notificationBatch.InstallDestinationChangedCharts.Single();
            Assert.AreEqual(@"C:\Overlay", changedChart.InstallDestination);
        });
    }

    [TestMethod]
    public void ApplyInstalledChartStorageTargets_PublishesSourceRefreshWithoutOverlayCharts()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            var addedFile = new TestableBmsFile
            {
                path = @"C:\Library\added.bms"
            };
            addedFile.SetHash("cccccccccccccccccccccccccccccccc");

            InvokeApplyInstalledChartStorageTargets(
                library,
                ChartStorageTargetSet.FromRows([addedFile], []));

            NormalLibraryRefreshNotificationBatch notificationBatch = library.GetNormalLibraryRefreshNotificationsAfter(0);
            Assert.AreNotEqual(0, notificationBatch.LatestVersion);
            Assert.IsFalse(notificationBatch.ResetsPriorNotifications);
            Assert.IsTrue(notificationBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
            Assert.IsTrue(notificationBatch.HasEffect(LibraryChartRefreshEffects.InstallDestinationOverlayChanged));
            Assert.AreEqual(0, notificationBatch.InstallDestinationChangedCharts.Count);
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_BmsInstallDestinationClearHidesStaleOwnerWarningThroughModelOverlay()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            var file = new TestableBmsFile
            {
                path = @"C:\Library\chart.bms"
            };
            file.SetHash("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            file.SetWarning(ChartWarningKind.InstallEstimationAmbiguous, "ambiguous");
            library.BMSFiles = [file];

            var delta = new LibraryMutationDelta();
            delta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
            {
                Chart = ChartFileProjection.WithPackageState(
                    ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: true),
                    @"C:\Deleted",
                    "Deleted title",
                    "Deleted artist",
                    [@"C:\Deleted", @"C:\Other"],
                    file.Warnings.ToStructuredList()),
                ClearInstallDestinationState = true
            });

            InvokeApplyLibraryMutationDelta(library, delta);

            Assert.IsTrue(file.Warnings.ToStructuredList().Any(warning => warning.Category == ChartWarningCategory.InstallEstimation));
            ChartFile changedChart = library.GetNormalLibraryRefreshNotificationsAfter(0).InstallDestinationChangedCharts.Single();
            Assert.AreEqual(string.Empty, changedChart.InstallDestination);
            Assert.IsFalse(changedChart.Warnings.Any(warning => warning.Category == ChartWarningCategory.InstallEstimation));
            ChartFile installedChart = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshotWithInstallDestinationOverlay(library).Single();
            Assert.AreEqual(string.Empty, installedChart.InstallDestination);
            Assert.IsFalse(installedChart.Warnings.Any(warning => warning.Category == ChartWarningCategory.InstallEstimation));
            Assert.AreEqual(
                string.Empty,
                ChartWarningProjectionFormatter.BuildDigestText(installedChart, ResourceHealthWarningProjection.Empty, hasResourceHealthProjection: false));
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_StalePathOverlayDoesNotLeakToReplacementOwner()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            var originalFile = new TestableBmsFile
            {
                path = @"C:\Library\chart.bms"
            };
            originalFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            library.BMSFiles = [originalFile];
            var delta = new LibraryMutationDelta();
            delta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
            {
                Chart = ChartFileProjection.FromBmsFile(originalFile, includeWarningSnapshot: false),
                NewInstallDestination = @"C:\Overlay"
            });
            InvokeApplyLibraryMutationDelta(library, delta);

            var replacementFile = new TestableBmsFile
            {
                path = originalFile.path
            };
            replacementFile.SetHash("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            library.BMSFiles = [replacementFile];

            ChartFile installedChart = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshotWithInstallDestinationOverlay(library).Single();

            Assert.AreSame(replacementFile, installedChart.GetBmsStorageOwner());
            Assert.AreEqual(string.Empty, installedChart.InstallDestination);
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_BuiltInstalledLookupMovesPathIncrementally()
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
            Directory.CreateDirectory(newDirectoryPath);
            File.WriteAllText(oldChartPath, "#PLAYER 1");
            File.WriteAllText(newChartPath, "#PLAYER 1");
            try
            {
                string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                var file = new TestableBmsFile
                {
                    path = oldChartPath
                };
                file.SetHash(hash);
                library.BMSFiles = [file];
                InstalledChartLookupIndexSnapshot initial = InvokeCreateInstalledChartLookupSnapshot(library);
                Assert.IsTrue(IsInstalledChartLookupIndexInitialized(library));
                CollectionAssert.AreEqual(new[] { oldDirectoryPath }, initial.Md5Directories[hash].ToArray());

                var delta = new LibraryMutationDelta
                {
                    InvalidateInstalledDirectoryIndex = true
                };
                delta.ChartPathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsFile(file),
                    NewPath = newChartPath
                });

                InvokeApplyLibraryMutationDelta(library, delta);
                InstalledChartLookupIndexSnapshot updated = InvokeCreateInstalledChartLookupSnapshot(library);

                Assert.IsTrue(IsInstalledChartLookupIndexInitialized(library));
                Assert.AreEqual(newChartPath, file.path);
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
            var firstFile = new TestableBmsFile
            {
                path = Path.Combine(firstDirectoryPath, "chart.bms")
            };
            firstFile.SetHash(firstHash);
            var secondFile = new TestableBmsFile
            {
                path = Path.Combine(secondDirectoryPath, "chart.bms")
            };
            secondFile.SetHash(secondHash);

            library.BMSFiles = [firstFile];
            InstalledChartLookupIndexSnapshot initial = InvokeCreateInstalledChartLookupSnapshot(library);
            Assert.IsTrue(IsInstalledChartLookupIndexInitialized(library));
            Assert.IsTrue(initial.ContainsPrimaryHash(firstHash));

            library.BMSFiles = [secondFile];

            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));
            InstalledChartLookupIndexSnapshot rebuilt = InvokeCreateInstalledChartLookupSnapshot(library);
            Assert.IsFalse(rebuilt.ContainsPrimaryHash(firstHash));
            Assert.IsTrue(rebuilt.ContainsPrimaryHash(secondHash));
            CollectionAssert.AreEqual(new[] { secondDirectoryPath }, rebuilt.Md5Directories[secondHash].ToArray());
        });
    }

    [TestMethod]
    public void RenameChartFolder_RewritesBmsonInstallDestinationFromModelOverlay()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_BmsonOverlayRename_" + Guid.NewGuid().ToString("N"));
            string sourceDirectoryPath = Path.Combine(tempRootPath, "InstallSource");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "InstallRenamed");
            string libraryDirectoryPath = Path.Combine(tempRootPath, "Library");
            Directory.CreateDirectory(sourceDirectoryPath);
            Directory.CreateDirectory(libraryDirectoryPath);
            try
            {
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                var bmsonSong = new LR2SongDBExtended.bmson_song
                {
                    path = Path.Combine(libraryDirectoryPath, "chart.bmson"),
                    folder = libraryDirectoryPath,
                    md5 = "abcdefabcdefabcdefabcdefabcdefab",
                    sha256 = "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd",
                    title = "Overlay Bmson"
                };
                library.BmsonSongs = [bmsonSong];

                var seedDelta = new LibraryMutationDelta();
                seedDelta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
                {
                    Chart = ChartFileProjection.FromBmsonSong(bmsonSong, includeWarningSnapshot: false),
                    NewInstallDestination = sourceDirectoryPath
                });
                InvokeApplyLibraryMutationDelta(library, seedDelta);
                library.RenameChartFolder(sourceDirectoryPath, "InstallRenamed");

                ChartFile changedChart = library.GetNormalLibraryRefreshNotificationsAfter(0).InstallDestinationChangedCharts.Single();
                Assert.AreSame(bmsonSong, changedChart.GetBmsonStorageOwner());
                Assert.AreEqual(destinationDirectoryPath, changedChart.InstallDestination);
                Assert.IsTrue(Directory.Exists(destinationDirectoryPath));
                Assert.IsFalse(Directory.Exists(sourceDirectoryPath));
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
                var sourceSong = new LR2SongDBExtended.bmson_song
                {
                    path = sourceChartPath,
                    folder = sourceDirectoryPath,
                    title = "Repair Duplicate Bmson",
                    md5 = md5,
                    sha256 = sha256
                };
                var installedSong = new LR2SongDBExtended.bmson_song
                {
                    path = installedChartPath,
                    folder = destinationDirectoryPath,
                    title = "Installed Duplicate Bmson",
                    md5 = md5,
                    sha256 = sha256
                };
                string movedSource = Path.Combine(sourceDirectoryPath, "other.bmson");
                string movedDestination = Path.Combine(destinationDirectoryPath, "other.bmson");
                File.WriteAllText(movedSource, "{}");
                string siblingSourcePath = Path.Combine(sourceDirectoryPath, "sibling.bmson");
                File.WriteAllText(siblingSourcePath, "{}");
                var movedSong = new LR2SongDBExtended.bmson_song
                {
                    path = movedSource,
                    folder = sourceDirectoryPath,
                    title = "Independent repair target",
                    md5 = "11111111111111111111111111111111",
                    sha256 = "1111111111111111111111111111111111111111111111111111111111111111"
                };
                var siblingSong = new LR2SongDBExtended.bmson_song
                {
                    path = siblingSourcePath,
                    folder = sourceDirectoryPath,
                    title = "Unselected sibling",
                    md5 = "22222222222222222222222222222222",
                    sha256 = "2222222222222222222222222222222222222222222222222222222222222222"
                };
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    songDb.InsertOrReplace(sourceSong, typeof(LR2SongDBExtended.bmson_song));
                    songDb.InsertOrReplace(installedSong, typeof(LR2SongDBExtended.bmson_song));
                    songDb.InsertOrReplace(movedSong, typeof(LR2SongDBExtended.bmson_song));
                    songDb.InsertOrReplace(siblingSong, typeof(LR2SongDBExtended.bmson_song));
                    if (catalogFailure)
                        songDb.Execute("CREATE TRIGGER fail_repair_delete BEFORE DELETE ON bmson_song WHEN OLD.path = '"
                            + sourceChartPath.Replace("'", "''") + "' BEGIN SELECT RAISE(ABORT, 'repair-deletion-fault'); END;");
                }
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                library.BmsonSongs = [sourceSong, installedSong, movedSong, siblingSong];
                ChartFile repairTarget = ChartFileProjection.WithPackageState(
                    ChartFileProjection.FromBmsonSong(sourceSong),
                    destinationDirectoryPath,
                    string.Empty,
                    string.Empty,
                    []);

                ChartFile independentTarget = ChartFileProjection.WithPackageState(
                    ChartFileProjection.FromBmsonSong(movedSong), destinationDirectoryPath, string.Empty, string.Empty, []);
                IReadOnlyList<string> approvedPaths = approveDuplicateRemoval ? [sourceChartPath] : [];
                LibraryFixInstallationResult repairResult = library.FixInstallationDirectoryCharts(
                    [independentTarget, repairTarget],
                    approvedPaths);
                if (approveDuplicateRemoval && catalogFailure)
                {
                    Assert.IsNotNull(repairResult.Failure);
                    Assert.IsNotNull(repairResult.RemovalOutcome);
                    Assert.AreEqual(1, repairResult.RemovalOutcome.ConfirmedChartCount);
                    Assert.IsTrue(repairResult.RemovalOutcome.CatalogApplyAttempted);
                    Assert.IsFalse(repairResult.RemovalOutcome.CatalogDurable);
                    Assert.IsNotNull(repairResult.RemovalOutcome.CatalogFailure);
                }
                else if (approveDuplicateRemoval)
                {
                    LibraryChartRemovalOutcome outcome = repairResult.RemovalOutcome;
                    Assert.AreEqual(1, outcome.ConfirmedChartCount);
                    Assert.IsFalse(outcome.HasError);
                }
                else
                {
                    Assert.IsNull(repairResult.Failure);
                    Assert.IsNull(repairResult.RemovalOutcome);
                    Assert.AreEqual(1, repairResult.DuplicateSkippedCount);
                }
                Assert.IsFalse(File.Exists(movedSource));
                Assert.IsTrue(File.Exists(movedDestination));

                Assert.AreEqual(approveDuplicateRemoval, !File.Exists(sourceChartPath));
                Assert.IsTrue(File.Exists(installedChartPath));
                Assert.IsTrue(File.Exists(siblingSourcePath));
                Assert.AreEqual(!approveDuplicateRemoval || catalogFailure, library.BmsonSongs.Any(song => string.Equals(song.path, sourceChartPath, StringComparison.OrdinalIgnoreCase)));
                Assert.IsTrue(library.BmsonSongs.Any(song => string.Equals(song.path, installedChartPath, StringComparison.OrdinalIgnoreCase)));
                Assert.IsTrue(library.BmsonSongs.Any(song => string.Equals(song.path, siblingSourcePath, StringComparison.OrdinalIgnoreCase)));
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    Assert.AreEqual(approveDuplicateRemoval && catalogFailure ? 1 : approveDuplicateRemoval ? 0 : 1,
                        songDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == sourceChartPath));
                    Assert.AreEqual(1, songDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == movedDestination));
                    Assert.AreEqual(0, songDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == movedSource));
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
                var file = BMSFile.CreateBMSFileFromFile(chartPath);
                file.SetMaintenanceInfo(new BMSFileMaintenanceInfo(file)
                {
                    hash = file.hash,
                    encoding = "unknown",
                    is_encoding_fixed = false
                }, suppressPropertyChanged: true);
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
                file.PropertyChanged += delegate (object? sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSFile.maintenanceInfo))
                    {
                        Interlocked.Increment(ref encodingChangedCount);
                    }
                };
                library.BMSFiles = [file];
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(new BMSFileMaintenanceInfo
                    {
                        path = chartPath,
                        hash = file.hash,
                        encoding = "unknown",
                        is_encoding_fixed = false
                    }, typeof(LR2SongDBExtended.maintenance));
                }
                Interlocked.Exchange(ref garbledChangedCount, 0);
                Interlocked.Exchange(ref garbledFixedChangedCount, 0);
                Interlocked.Exchange(ref encodingChangedCount, 0);

                library.SetBMSFilesEncoding([file], "gb2312");

                Assert.AreEqual(0, Volatile.Read(ref garbledChangedCount));
                Assert.AreEqual(0, Volatile.Read(ref garbledFixedChangedCount));
                Assert.IsTrue(Volatile.Read(ref encodingChangedCount) > 0);
                Assert.AreEqual("gb2312", file.maintenanceInfo.encoding);
                using var verifySongDb = new LR2SongDBExtended(songDbPath);
                BMSFileMaintenanceInfo persistedMaintenance = verifySongDb.Table<BMSFileMaintenanceInfo>().Single(row => row.path == chartPath);
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
            var file = new TestableBmsFile
            {
                path = @"C:\Library\chart.bms"
            };
            file.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var bmsFilesPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var table = new BMSTable
            {
                name = "Before",
                symbol = "A",
                entries = [new BMSTableEntry(file)]
            };
            int bmsFilesChangedCount = 0;
            int filePropertyChangedCount = 0;
            library.PropertyChanged += delegate (object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(BMSLibrary.BMSFiles))
                {
                    Interlocked.Increment(ref bmsFilesChangedCount);
                    bmsFilesPublished.TrySetResult(true);
                }
            };
            file.PropertyChanged += delegate
            {
                Interlocked.Increment(ref filePropertyChangedCount);
            };
            library.BMSFiles = [file];
            await bmsFilesPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Interlocked.Exchange(ref bmsFilesChangedCount, 0);
            Interlocked.Exchange(ref filePropertyChangedCount, 0);
            library.RefreshReferenceDisplayForTable(table);
            ChartFile chart = ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false);

            table.symbol = "B";
            table.name = "After";
            Assert.AreEqual("A", library.GetPlaylistReferenceDisplay(chart).Symbols);
            Assert.AreEqual("Before", library.GetPlaylistReferenceDisplay(chart).Names);

            library.RefreshReferenceDisplayForTable(table);

            Assert.AreEqual(0, Volatile.Read(ref filePropertyChangedCount));
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
            var file = new TestableBmsFile
            {
                path = @"C:\Library\chart.bms"
            };
            file.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            BMSTable oldTable = CreateTable("Before", "A", file.hash);
            BMSTable newTable = CreateTable("After", "B", file.hash);
            library.BMSFiles = [file];

            library.AddReferenceBMSTables(oldTable);
            ChartFile chart = ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false);
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
            var file = new TestableBmsFile
            {
                path = @"C:\Library\chart.bms"
            };
            file.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            BMSTable currentTable = CreateTable("Before", "A", file.hash);
            BMSTable replacementTable = CreateTable("After", "B", file.hash);
            library.BMSFiles = [file];
            library.AddReferenceBMSTables(currentTable);
            ChartFile chart = ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false);

            var stalePlan = library.PrepareReferenceBMSTableSynchronization([replacementTable]);
            Assert.AreEqual("A", library.GetPlaylistReferenceDisplay(chart).Symbols);

            currentTable.symbol = "C";
            currentTable.name = "Concurrent";
            library.RefreshReferenceDisplayForTable(currentTable);

            Assert.IsFalse(library.TryCommitReferenceBMSTableSynchronization(stalePlan));
            Assert.AreEqual("C", library.GetPlaylistReferenceDisplay(chart).Symbols);
            Assert.AreEqual("Concurrent", library.GetPlaylistReferenceDisplay(chart).Names);

            var currentPlan = library.PrepareReferenceBMSTableSynchronization([replacementTable]);
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

            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
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

            Assert.IsNull(matchingBmsonEntry.GetBmsOwnerForTest());
            Assert.IsNull(unmatchedBmsonEntry.GetBmsOwnerForTest());
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
            LR2SongDBExtended.bmson_song song = new LR2SongDBExtended.bmson_song
            {
                path = @"C:\Pending\Package\matching.bmson",
                md5 = matchingHash,
                sha256 = new string('b', 64),
                title = "Pending Bmson",
                artist = "Artist"
            };
            PackageChartEntry matchingBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(song));
            library.ChartPackagesPending = CreatePackageCollection(
            [
                ChartPackage.FromChartEntries([matchingBmsonEntry])
            ]);
            BMSTable table = CreateTable("Matched", "M", matchingHash);

            library.AddReferenceBMSTables(table);

            Assert.IsNull(matchingBmsonEntry.GetBmsOwnerForTest());
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
            var song = new LR2SongDBExtended.bmson_song
            {
                path = @"C:\Library\chart.bmson",
                md5 = matchingHash,
                sha256 = new string('b', 64),
                title = "Installed Bmson",
                artist = "Artist"
            };
            library.BmsonSongs = [song];
            ChartFile chart = ChartFileProjection.FromBmsonSong(song);
            BMSTable table = CreateTable("Matched", "M", matchingHash);

            library.AddReferenceBMSTables(table);

            Assert.IsNull(chart.GetBmsStorageOwner());
            Assert.AreSame(song, chart.GetBmsonStorageOwner());
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
            var file = new TestableBmsFile
            {
                path = @"C:\Library\chart.bms"
            };
            file.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            BMSTable table = CreateTable("Matched", "M", file.hash);

            library.AddReferenceBMSTablesToCharts(table, [ChartFileProjection.FromBmsFile(file)]);

            ChartFile chart = ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false);
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
            var song = new LR2SongDBExtended.bmson_song
            {
                path = @"C:\Library\chart.bmson",
                md5 = matchingHash,
                sha256 = new string('b', 64),
                title = "Library Bmson",
                artist = "Artist"
            };
            ChartFile chart = ChartFileProjection.FromBmsonSong(song);
            BMSTable table = CreateTable("Matched", "M", matchingHash);

            library.AddReferenceBMSTablesToCharts(table, [chart]);

            Assert.IsNull(chart.GetBmsStorageOwner());
            Assert.AreSame(song, chart.GetBmsonStorageOwner());
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
            ChartPackage installedPackage = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
            BMSTable table = CreateTable("Unmatched", "U", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

            library.AddReferenceBMSTablesToPackageCharts([table], [installedPackage]);

            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
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
            ChartPackage installedPackage = ChartPackage.FromChartEntries([matchingBmsonEntry, unmatchedBmsonEntry]);
            BMSTable table = CreateTable("Matched", "M", matchingHash);
            library.AddReferenceBMSTables([table]);

            library.AddReferenceBMSTablesToPackageCharts([table], [installedPackage]);

            Assert.IsNull(matchingBmsonEntry.GetBmsOwnerForTest());
            Assert.IsNull(unmatchedBmsonEntry.GetBmsOwnerForTest());
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
            Assert.IsNull(oldOnlyBmsonEntry.GetBmsOwnerForTest());
            Assert.AreEqual("O", library.GetPlaylistReferenceDisplay(oldOnlyBmsonEntry.Chart).Symbols);

            library.ReplaceReferenceBMSTable(oldTable, newTable);

            Assert.IsNull(oldOnlyBmsonEntry.GetBmsOwnerForTest());
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

            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
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
            Assert.IsNull(matchingBmsonEntry.GetBmsOwnerForTest());
            Assert.AreEqual("S", library.GetPlaylistReferenceDisplay(matchingBmsonEntry.Chart).Symbols);

            List<BMSTableEntry> removedEntries = [.. table.entries];
            table.entries.Clear();
            library.RemoveReferenceBMSTables(table, removedEntries);

            Assert.IsNull(matchingBmsonEntry.GetBmsOwnerForTest());
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
            var file = new TestableBmsFile
            {
                path = @"C:\Library\chart.bms"
            };
            string md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
            file.SetHash(md5);
            library.BMSFiles = [file];
            BMSTable table = CreateTable("Reference", "R", md5);

            library.AddReferenceBMSTables(table);
            ChartFile chart = ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false);
            Assert.AreEqual("R", library.GetPlaylistReferenceDisplay(chart).Symbols);

            table.MarkEntriesNotLoaded();
            library.RemoveReferenceBMSTables([table]);

            Assert.AreEqual(string.Empty, library.GetPlaylistReferenceDisplay(chart).Symbols);
        });
    }

    private static void InvokeApplyLibraryMutationDelta(BMSLibrary library, LibraryMutationDelta delta)
    {
        library.ApplyLibraryMutationDelta(delta);
    }

    private static void InvokeApplyInstalledChartStorageTargets(BMSLibrary library, ChartStorageTargetSet targets)
    {
        library.ApplyInstalledChartStorageTargets(targets, "install_package");
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
        return PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
        {
            path = path,
            md5 = md5,
            sha256 = string.IsNullOrWhiteSpace(sha256) ? new string('b', 64) : sha256,
            title = "Pending Bmson",
            artist = "Artist"
        }));
    }

    private static void WithTemporarySongDb(Action<string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_FolderRenameRefresh_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
            }
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
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
            }
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

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
        }

        public void SetSha256(string value)
        {
            ApplySha256(value);
        }

        public void SetTitle(string value)
        {
            title = value;
        }

        public void SetArtist(string value)
        {
            artist = value;
        }

        public void SetFavorite(int? value)
        {
            favorite = value;
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
        var testableBmsFile = new TestableBmsFile();
        testableBmsFile.SetHash(md5);
        testableBmsFile.SetSha256(sha256);
        testableBmsFile.path = @"C:\Library\chart.bms";
        BMSTableEntry entry = new BMSTableEntry(testableBmsFile)
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
