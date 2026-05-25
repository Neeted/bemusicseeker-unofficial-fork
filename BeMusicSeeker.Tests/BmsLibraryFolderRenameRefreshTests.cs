using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
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
    public void RenameChartFolder_UpdatesFolderCellWithoutRaisingLibraryChartsChanged()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RenameRefresh_" + Guid.NewGuid().ToString("N"));
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Source");
            string chartPath = Path.Combine(sourceDirectoryPath, "chart.bms");
            Directory.CreateDirectory(sourceDirectoryPath);
            File.WriteAllText(chartPath, "#PLAYER 1");
            try
            {
                var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                var file = new TestableBmsFile
                {
                    path = chartPath
                };
                int bmsFilesChangedCount = 0;
                int folderChangedCount = 0;
                int pathChangedCount = 0;
                library.PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSLibrary.BMSFiles))
                    {
                        Interlocked.Increment(ref bmsFilesChangedCount);
                    }
                };
                SetLibraryFilesWithoutNotification(library, [file]);
                Interlocked.Exchange(ref bmsFilesChangedCount, 0);
                file.PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSFile.Folder))
                    {
                        Interlocked.Increment(ref folderChangedCount);
                    }
                    if (e.PropertyName == nameof(BMSFile.path))
                    {
                        Interlocked.Increment(ref pathChangedCount);
                    }
                };

                library.RenameChartFolder(sourceDirectoryPath, "Renamed");

                Assert.IsTrue(WaitUntilTrue(() => Volatile.Read(ref folderChangedCount) > 0));
                Assert.IsTrue(WaitUntilTrue(() => Volatile.Read(ref pathChangedCount) > 0));
                Assert.AreEqual(0, Volatile.Read(ref bmsFilesChangedCount));
                Assert.IsTrue(Volatile.Read(ref folderChangedCount) > 0);
                Assert.IsTrue(Volatile.Read(ref pathChangedCount) > 0);
                Assert.AreEqual("Renamed", file.Folder);
                Assert.IsTrue(file.path.Contains(Path.Combine("Renamed", "chart.bms")));
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
    public void MoveLibraryRootFolder_BmsChart_RaisesLibraryChartsChanged()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
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
                var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                var file = new TestableBmsFile
                {
                    path = chartPath
                };
                int bmsFilesChangedCount = 0;
                library.PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSLibrary.BMSFiles))
                    {
                        Interlocked.Increment(ref bmsFilesChangedCount);
                    }
                };
                SetLibraryFilesWithoutNotification(library, [file]);
                Interlocked.Exchange(ref bmsFilesChangedCount, 0);

                library.MoveLibraryRootFolder([LibraryChartRef.FromChartFile(ChartFileProjection.FromBmsFile(file))], destinationParentPath);

                Assert.IsTrue(WaitUntilTrue(() => Volatile.Read(ref bmsFilesChangedCount) > 0));
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
    public void RenameBmsonFolder_UpdatesFolderWithoutRaisingCollectionRefresh()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_BmsonRenameRefresh_" + Guid.NewGuid().ToString("N"));
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Source");
            string chartPath = Path.Combine(sourceDirectoryPath, "chart.bmson");
            Directory.CreateDirectory(sourceDirectoryPath);
            File.WriteAllText(chartPath, "{}");
            try
            {
                var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                var song = new LR2SongDBExtended.bmson_song
                {
                    path = chartPath,
                    folder = sourceDirectoryPath,
                    title = "Chart"
                };
                int bmsFilesChangedCount = 0;
                int bmsonSongsChangedCount = 0;
                library.PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSLibrary.BMSFiles))
                    {
                        Interlocked.Increment(ref bmsFilesChangedCount);
                    }
                    if (e.PropertyName == nameof(BMSLibrary.BmsonSongs))
                    {
                        Interlocked.Increment(ref bmsonSongsChangedCount);
                    }
                };
                SetLibraryBmsonSongsWithoutNotification(library, [song]);
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

    [TestMethod]
    public void MoveLibraryRootFolder_BmsonChart_RaisesLibraryChartsChanged()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
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
                var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                var song = new LR2SongDBExtended.bmson_song
                {
                    path = chartPath,
                    folder = sourceRootPath,
                    title = "Chart"
                };
                int bmsFilesChangedCount = 0;
                library.PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSLibrary.BMSFiles))
                    {
                        Interlocked.Increment(ref bmsFilesChangedCount);
                    }
                };
                SetLibraryBmsonSongsWithoutNotification(library, [song]);
                Interlocked.Exchange(ref bmsFilesChangedCount, 0);

                library.MoveLibraryRootFolder([LibraryChartRef.FromChartFile(ChartFileProjection.FromBmsonSong(song))], destinationParentPath);

                Assert.IsTrue(WaitUntilTrue(() => Volatile.Read(ref bmsFilesChangedCount) > 0));
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
                var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                SetLibraryBmsonSongsWithoutNotification(library, [song]);
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

    [TestMethod]
    public void FixInstallationDirectoryCharts_BmsChartClearsInstallDestinationOverlayAfterApply()
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
            File.WriteAllText(sourceChartPath, "#PLAYER 1");
            try
            {
                var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                var file = new TestableBmsFile
                {
                    path = sourceChartPath
                };
                file.SetHash("cccccccccccccccccccccccccccccccc");
                SetLibraryFilesWithoutNotification(library, [file]);
                ChartFile repairTarget = ChartFileProjection.WithPackageState(
                    ChartFileProjection.FromBmsFile(file),
                    destinationDirectoryPath,
                    string.Empty,
                    string.Empty,
                    []);

                library.FixInstallationDirectoryCharts([repairTarget]);

                Assert.AreEqual(destinationChartPath, file.path);
                ChartFile changedChart = library.GetLibraryChartChangeNotificationsAfter(0).InstallDestinationChangedCharts.Single();
                Assert.AreEqual(destinationChartPath, changedChart.Path);
                Assert.AreEqual(string.Empty, changedChart.InstallDestination);
                ChartFile installedChart = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshotWithInstallDestinationOverlay(library).Single();
                Assert.AreEqual(destinationChartPath, installedChart.Path);
                Assert.AreEqual(string.Empty, installedChart.InstallDestination);
                Assert.IsFalse(File.Exists(sourceChartPath));
                Assert.IsTrue(File.Exists(destinationChartPath));
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
            var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            var file = new TestableBmsFile
            {
                path = @"C:\Library\chart.bms"
            };
            file.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            SetLibraryFilesWithoutNotification(library, [file]);

            var delta = new LibraryMutationDelta();
            delta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
            {
                Chart = ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false),
                NewInstallDestination = @"C:\New"
            });

            InvokeApplyLibraryMutationDelta(library, delta);

            ChartFile changedChart = library.GetLibraryChartChangeNotificationsAfter(0).InstallDestinationChangedCharts.Single();
            Assert.AreEqual(@"C:\New", changedChart.InstallDestination);
            ChartFile installedChart = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshotWithInstallDestinationOverlay(library).Single();
            Assert.AreEqual(@"C:\New", installedChart.InstallDestination);
        });
    }

    [TestMethod]
    public void LibraryChartChangeNotifications_ExternalReplacementPublishesResetBarrier()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            var originalFile = new TestableBmsFile
            {
                path = @"C:\Library\chart.bms"
            };
            originalFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            SetLibraryFilesWithoutNotification(library, [originalFile]);

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

            LibraryChartChangeNotificationBatch notificationBatch = library.GetLibraryChartChangeNotificationsAfter(0);
            Assert.IsTrue(notificationBatch.ResetsPriorNotifications);
            Assert.AreEqual(0, notificationBatch.InstallDestinationChangedCharts.Count);
        });
    }

    [TestMethod]
    public void ApplyInstalledChartStorageTargets_DoesNotResetPriorOverlayNotification()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            var originalFile = new TestableBmsFile
            {
                path = @"C:\Library\chart.bms"
            };
            originalFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            SetLibraryFilesWithoutNotification(library, [originalFile]);

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
                ChartStorageTargetSet.FromRows([addedFile], []),
                "test");

            LibraryChartChangeNotificationBatch notificationBatch = library.GetLibraryChartChangeNotificationsAfter(0);
            Assert.IsFalse(notificationBatch.ResetsPriorNotifications);
            ChartFile changedChart = notificationBatch.InstallDestinationChangedCharts.Single();
            Assert.AreEqual(@"C:\Overlay", changedChart.InstallDestination);
        });
    }

    [TestMethod]
    public void ApplyInstalledChartStorageTargets_DoesNotPublishEmptyOverlayNotification()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            var addedFile = new TestableBmsFile
            {
                path = @"C:\Library\added.bms"
            };
            addedFile.SetHash("cccccccccccccccccccccccccccccccc");

            InvokeApplyInstalledChartStorageTargets(
                library,
                ChartStorageTargetSet.FromRows([addedFile], []),
                "test");

            LibraryChartChangeNotificationBatch notificationBatch = library.GetLibraryChartChangeNotificationsAfter(0);
            Assert.AreEqual(0, notificationBatch.LatestVersion);
            Assert.IsFalse(notificationBatch.ResetsPriorNotifications);
            Assert.AreEqual(0, notificationBatch.InstallDestinationChangedCharts.Count);
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_BmsInstallDestinationClearHidesStaleOwnerWarningThroughModelOverlay()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            var file = new TestableBmsFile
            {
                path = @"C:\Library\chart.bms"
            };
            file.SetHash("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            file.SetWarning(ChartWarningKind.InstallEstimationAmbiguous, "ambiguous");
            SetLibraryFilesWithoutNotification(library, [file]);

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
            ChartFile changedChart = library.GetLibraryChartChangeNotificationsAfter(0).InstallDestinationChangedCharts.Single();
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
    public void ApplyLibraryMutationDelta_PathFallbackOverlayDoesNotLeakToDifferentOwnerAtSamePath()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
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
                var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                var file = new TestableBmsFile
                {
                    path = oldChartPath
                };
                file.SetHash(hash);
                SetLibraryFilesWithoutNotification(library, [file]);
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
            var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
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
                var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                var bmsonSong = new LR2SongDBExtended.bmson_song
                {
                    path = Path.Combine(libraryDirectoryPath, "chart.bmson"),
                    folder = libraryDirectoryPath,
                    md5 = "abcdefabcdefabcdefabcdefabcdefab",
                    sha256 = "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd",
                    title = "Overlay Bmson"
                };
                SetLibraryBmsonSongsWithoutNotification(library, [bmsonSong]);

                var seedDelta = new LibraryMutationDelta();
                seedDelta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
                {
                    Chart = ChartFileProjection.FromBmsonSong(bmsonSong, includeWarningSnapshot: false),
                    NewInstallDestination = sourceDirectoryPath
                });
                InvokeApplyLibraryMutationDelta(library, seedDelta);
                library.RenameChartFolder(sourceDirectoryPath, "InstallRenamed");

                ChartFile changedChart = library.GetLibraryChartChangeNotificationsAfter(0).InstallDestinationChangedCharts.Single();
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
    public void FixInstallationDirectoryCharts_BmsonDuplicateRemovesRepairSourceAndKeepsInstalledRow()
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
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    songDb.InsertOrReplace(sourceSong, typeof(LR2SongDBExtended.bmson_song));
                    songDb.InsertOrReplace(installedSong, typeof(LR2SongDBExtended.bmson_song));
                }
                var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                SetLibraryBmsonSongsWithoutNotification(library, [sourceSong, installedSong]);
                ChartFile repairTarget = ChartFileProjection.WithPackageState(
                    ChartFileProjection.FromBmsonSong(sourceSong),
                    destinationDirectoryPath,
                    string.Empty,
                    string.Empty,
                    []);

                library.FixInstallationDirectoryCharts([repairTarget]);

                Assert.IsFalse(File.Exists(sourceChartPath));
                Assert.IsTrue(File.Exists(installedChartPath));
                Assert.IsFalse(library.BmsonSongs.Any(song => string.Equals(song.path, sourceChartPath, StringComparison.OrdinalIgnoreCase)));
                Assert.IsTrue(library.BmsonSongs.Any(song => string.Equals(song.path, installedChartPath, StringComparison.OrdinalIgnoreCase)));
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    Assert.AreEqual(0, songDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == sourceChartPath));
                    Assert.AreEqual(1, songDb.Table<LR2SongDBExtended.bmson_song>().Count(row => row.path == installedChartPath));
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
    public void SetBMSFilesEncoding_UpdatesEncodingCellWithoutRaisingGarbledCollectionsChanged()
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
                var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
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
                library.PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSLibrary.BMSFilesGarbled))
                    {
                        Interlocked.Increment(ref garbledChangedCount);
                    }
                    if (e.PropertyName == nameof(BMSLibrary.BMSFilesGarbledFixed))
                    {
                        Interlocked.Increment(ref garbledFixedChangedCount);
                    }
                };
                file.PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSFile.maintenanceInfo))
                    {
                        Interlocked.Increment(ref encodingChangedCount);
                    }
                };
                SetLibraryFilesWithoutNotification(library, [file]);
                Interlocked.Exchange(ref garbledChangedCount, 0);
                Interlocked.Exchange(ref garbledFixedChangedCount, 0);
                Interlocked.Exchange(ref encodingChangedCount, 0);

                library.SetBMSFilesEncoding([file], "gb2312");

                Thread.Sleep(200);
                Assert.AreEqual(0, Volatile.Read(ref garbledChangedCount));
                Assert.AreEqual(0, Volatile.Read(ref garbledFixedChangedCount));
                Assert.IsTrue(Volatile.Read(ref encodingChangedCount) > 0);
                Assert.AreEqual("gb2312", file.maintenanceInfo.encoding);
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
    public void RefreshReferenceDisplayForTable_UpdatesPlaylistCellWithoutRaisingLibraryChartsChanged()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            var file = new TestableBmsFile
            {
                path = @"C:\Library\chart.bms"
            };
            file.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var table = new BMSTable
            {
                name = "Before",
                symbol = "A",
                entries = [new BMSTableEntry(file)]
            };
            int bmsFilesChangedCount = 0;
            int filePropertyChangedCount = 0;
            library.PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(BMSLibrary.BMSFiles))
                {
                    Interlocked.Increment(ref bmsFilesChangedCount);
                }
            };
            file.PropertyChanged += delegate
            {
                Interlocked.Increment(ref filePropertyChangedCount);
            };
            SetLibraryFilesWithoutNotification(library, [file]);
            Interlocked.Exchange(ref bmsFilesChangedCount, 0);
            Interlocked.Exchange(ref filePropertyChangedCount, 0);
            library.RefreshReferenceDisplayForTable(table);
            ChartFile chart = ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false);

            table.symbol = "B";
            table.name = "After";
            Assert.AreEqual("A", library.GetPlaylistReferenceDisplay(chart).Symbols);
            Assert.AreEqual("Before", library.GetPlaylistReferenceDisplay(chart).Names);

            library.RefreshReferenceDisplayForTable(table);

            Assert.IsFalse(WaitUntilTrue(() => Volatile.Read(ref filePropertyChangedCount) > 0, timeoutMs: 100));
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
            var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            var file = new TestableBmsFile
            {
                path = @"C:\Library\chart.bms"
            };
            file.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            BMSTable oldTable = CreateTable("Before", "A", file.hash);
            BMSTable newTable = CreateTable("After", "B", file.hash);
            SetLibraryFilesWithoutNotification(library, [file]);

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
    public void AddReferenceBMSTables_DoesNotMaterializeUnmatchedPendingBmsonEntries()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
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
            var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
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
            var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
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
            var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            string matchingHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            var song = new LR2SongDBExtended.bmson_song
            {
                path = @"C:\Library\chart.bmson",
                md5 = matchingHash,
                sha256 = new string('b', 64),
                title = "Installed Bmson",
                artist = "Artist"
            };
            SetLibraryBmsonSongsWithoutNotification(library, [song]);
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
            var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
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
            var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
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
            var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
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
            var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
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
            var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
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
            var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
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
            var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
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

    private static bool WaitUntilTrue(Func<bool> predicate, int timeoutMs = 2000)
    {
        return SpinWait.SpinUntil(predicate, timeoutMs);
    }

    private static void SetLibraryFilesWithoutNotification(BMSLibrary library, IEnumerable<BMSFile> files)
    {
        FieldInfo fieldInfo = typeof(BMSLibrary).GetField("_BMSFiles", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(fieldInfo);
        fieldInfo.SetValue(library, files.ToList());
    }

    private static void SetLibraryBmsonSongsWithoutNotification(BMSLibrary library, IEnumerable<LR2SongDBExtended.bmson_song> songs)
    {
        FieldInfo fieldInfo = typeof(BMSLibrary).GetField("_BmsonSongs", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(fieldInfo);
        fieldInfo.SetValue(library, songs.ToList());
    }

    private static void InvokeApplyLibraryMutationDelta(BMSLibrary library, LibraryMutationDelta delta)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("ApplyLibraryMutationDelta", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        methodInfo.Invoke(library, [delta]);
    }

    private static void InvokeApplyInstalledChartStorageTargets(BMSLibrary library, ChartStorageTargetSet targets, string reason)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("ApplyInstalledChartStorageTargets", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        methodInfo.Invoke(library, [targets, reason]);
    }

    private static InstalledChartLookupIndexSnapshot InvokeCreateInstalledChartLookupSnapshot(BMSLibrary library)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("CreateInstalledChartLookupSnapshotUnsafe", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        return (InstalledChartLookupIndexSnapshot)methodInfo.Invoke(library, []);
    }

    private static bool IsInstalledChartLookupIndexInitialized(BMSLibrary library)
    {
        FieldInfo fieldInfo = typeof(BMSLibrary).GetField("installedChartLookupIndexInitialized", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(fieldInfo);
        return (bool)fieldInfo.GetValue(library);
    }

    private static List<ChartFile> InvokeCreateOwnedChartInfoFullBackfillTargetSnapshotWithInstallDestinationOverlay(BMSLibrary library)
    {
        List<ChartFile> snapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
        MethodInfo overlayMethodInfo = typeof(BMSLibrary).GetMethod("OverlayInstallDestinationRuntimeStates", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(overlayMethodInfo);
        return (List<ChartFile>)overlayMethodInfo.Invoke(library, [snapshot]);
    }

    private static List<ChartFile> InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(BMSLibrary library)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("CreateOwnedChartInfoFullBackfillTargetSnapshot", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        return (List<ChartFile>)methodInfo.Invoke(library, []);
    }

    private static DispatcherCollection<ChartPackage> CreatePackageCollection(IEnumerable<ChartPackage> packages)
    {
        return new DispatcherCollection<ChartPackage>(new ObservableCollection<ChartPackage>([.. (packages ?? [])]), Dispatcher.CurrentDispatcher);
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
        public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            return defaultResult == MessageBoxResult.None ? MessageBoxResult.OK : defaultResult;
        }
    }

    private sealed class TestFileMutationService : IFileMutationService
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
            string destinationDirectoryPath = Path.GetDirectoryName(destinationPath);
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
            if (overwrite && Directory.Exists(destinationPath))
            {
                Directory.Delete(destinationPath, recursive: true);
            }
            string destinationParentPath = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationParentPath))
            {
                Directory.CreateDirectory(destinationParentPath);
            }
            CopyDirectory(sourcePath, destinationPath);
            Directory.Delete(sourcePath, recursive: true);
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
                string destinationDirectoryPath = Path.GetDirectoryName(destinationFilePath);
                if (!string.IsNullOrWhiteSpace(destinationDirectoryPath))
                {
                    Directory.CreateDirectory(destinationDirectoryPath);
                }
                File.Copy(filePath, destinationFilePath, overwrite: true);
            }
        }
    }
}
