using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryInitializationServiceTests
{
    [TestMethod]
    public void LoadSongTable_FixesRelativePathsAndAppliesMaintenanceMap()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string rootedChartPath = Path.Combine(lr2RootPath, "Songs", "chart.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(rootedChartPath));
            File.WriteAllText(rootedChartPath, "#PLAYER 1");

            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();

                TestableBmsFile song = new TestableBmsFile
                {
                    path = Path.Combine("Songs", "chart.bms"),
                    folder = "folder",
                    parent = "parent"
                };
                song.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                songDb.InsertOrReplace(song, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(new LR2SongDB.folder
                {
                    path = "Songs\\",
                    title = "Songs",
                    parent = "e2977170",
                    type = 1
                }, typeof(LR2SongDB.folder));
                songDb.InsertOrReplace(new BMSFileMaintenanceInfo
                {
                    path = rootedChartPath,
                    hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    encoding = "shift_jis"
                }, typeof(LR2SongDBExtended.maintenance));
            }

            BmsLibraryInitializationService service = new BmsLibraryInitializationService();
            SongTableLoadResult result = service.LoadSongTable(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                null,
                new TestFileMutationService(),
                null,
                ex => ex.Message);

            Assert.AreEqual(1, result.LoadedFiles.Count);
            Assert.AreEqual(rootedChartPath, result.LoadedFiles[0].path);
            Assert.AreEqual("shift_jis", result.LoadedFiles[0].maintenanceInfo.encoding);
            Assert.AreEqual(1, result.RelativePathFixedCount);
            Assert.IsTrue(result.DbWriteRequired);
            CollectionAssert.Contains(result.DeletedSongPaths, Path.Combine("Songs", "chart.bms"));
            Assert.IsTrue(result.UpdatedSongs.Any((BMSFile file) => file.path == rootedChartPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_UsesPrefetchedScanAndClearsStaleInstallDestination()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string keepDirectoryPath = Path.Combine(lr2RootPath, "Keep");
            string newDirectoryPath = Path.Combine(lr2RootPath, "New");
            string staleDirectoryPath = Path.Combine(lr2RootPath, "Stale");
            Directory.CreateDirectory(keepDirectoryPath);
            Directory.CreateDirectory(newDirectoryPath);
            File.WriteAllText(Path.Combine(keepDirectoryPath, "keep.bms"), "#PLAYER 1\r\n#TITLE Keep\r\n");
            File.WriteAllText(Path.Combine(newDirectoryPath, "added.bms"), "#PLAYER 1\r\n#TITLE Added\r\n");

            TestableBmsFile keepFile = new TestableBmsFile
            {
                path = Path.Combine(keepDirectoryPath, "keep.bms"),
                instl_dst = staleDirectoryPath
            };
            keepFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            TestableBmsFile deletedFile = new TestableBmsFile
            {
                path = Path.Combine(lr2RootPath, "Deleted", "deleted.bms")
            };
            deletedFile.SetHash("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            using (LR2SongDBExtended songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            int executeScanCount = 0;
            BmsLibraryInitializationService service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                new[] { keepFile, deletedFile },
                new BmsScanExecutionResult
                {
                    Success = true,
                    Result = new BmsScanResult
                    {
                        BmsFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        {
                            keepFile.path,
                            Path.Combine(newDirectoryPath, "added.bms")
                        },
                        FilesByDirectory = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { keepDirectoryPath, new List<string> { keepFile.path } },
                            { newDirectoryPath, new List<string> { Path.Combine(newDirectoryPath, "added.bms") } }
                        }
                    }
                },
                123L,
                delegate
                {
                    Interlocked.Increment(ref executeScanCount);
                    return null;
                },
                null);

            Assert.AreEqual(0, executeScanCount);
            Assert.IsTrue(result.PrefetchedScanUsed);
            Assert.IsTrue(result.HasDbDiff);
            CollectionAssert.Contains(result.DeletedPaths, deletedFile.path);
            Assert.AreEqual(1, result.AddedFiles.Count);
            Assert.AreEqual(2, result.NextFiles.Count);
            Assert.AreSame(keepFile, result.ClearedInstallDestinations.Single());
            Assert.IsNull(keepFile.instl_dst);
            CollectionAssert.Contains(result.NextFolderAllFileList.Keys.ToList(), keepDirectoryPath);
            CollectionAssert.Contains(result.NextFolderAllFileList.Keys.ToList(), newDirectoryPath);

            using LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.song>();
            List<BMSFile> dbFiles = songDb.Table<BMSFile>().ToList();
            Assert.AreEqual(1, dbFiles.Count);
            Assert.AreEqual(Path.Combine(newDirectoryPath, "added.bms"), dbFiles[0].path);
        });
    }

    [TestMethod]
    public void RunInitialize_InvokesAllPhasesAndWaitsForContinuations()
    {
        BmsLibraryInitializationService service = new BmsLibraryInitializationService();
        int phase1Count = 0;
        int phase2Count = 0;
        int phase3Count = 0;
        int continuationCount = 0;

        InitializationExecutionResult result = service.RunInitialize(
            new List<Action>
            {
                delegate
                {
                    Interlocked.Increment(ref continuationCount);
                }
            },
            new SemaphoreSlim(2, 2),
            delegate
            {
                Interlocked.Increment(ref phase1Count);
            },
            delegate
            {
                Interlocked.Increment(ref phase2Count);
            },
            delegate
            {
                Interlocked.Increment(ref phase3Count);
            });

        Assert.AreEqual(1, phase1Count);
        Assert.AreEqual(1, phase2Count);
        Assert.AreEqual(1, phase3Count);
        Assert.AreEqual(1, continuationCount);
        Assert.IsTrue(result.Phase1MinLoadMs >= 0);
        Assert.IsTrue(result.Phase2ScanMaintMs >= 0);
        Assert.IsTrue(result.Phase3InstallMaintenanceMs >= 0);
        Assert.IsTrue(result.WaitContinuationMs >= 0);
        Assert.IsTrue(result.TotalMs >= 0);
    }

    [TestMethod]
    public void LoadInstallTable_InitializesPendingWarningsAndCounts()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string directoryPackagePath = Path.Combine(lr2RootPath, "PendingDir");
            Directory.CreateDirectory(directoryPackagePath);
            string directoryChartPath = Path.Combine(directoryPackagePath, "dir_chart.bms");
            File.WriteAllText(directoryChartPath, "#PLAYER 1\r\n#TITLE Dir\r\n");

            string singleFileDirectoryPath = Path.Combine(lr2RootPath, "Single");
            Directory.CreateDirectory(singleFileDirectoryPath);
            string singleFileChartPath = Path.Combine(singleFileDirectoryPath, "single_chart.bms");
            File.WriteAllText(singleFileChartPath, "#PLAYER 1\r\n#TITLE Single\r\n");

            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(new BMSPackage
                {
                    path = directoryPackagePath,
                    delete_parent = false
                }, typeof(LR2SongDBExtended.install));
                songDb.InsertOrReplace(new BMSPackage
                {
                    path = singleFileChartPath,
                    delete_parent = false
                }, typeof(LR2SongDBExtended.install));
                songDb.InsertOrReplace(new BMSPackage
                {
                    path = Path.Combine(lr2RootPath, "MissingPkg"),
                    delete_parent = false
                }, typeof(LR2SongDBExtended.install));
            }

            string installedHash = BMSFile.CreateBMSFileFromFile(directoryChartPath).hash;
            BmsLibraryInitializationService service = new BmsLibraryInitializationService();

            InstallTableLoadResult result = service.LoadInstallTable(
                new BmsLibraryDbGateway(songDbPath),
                hash => string.Equals(hash, installedHash, StringComparison.OrdinalIgnoreCase),
                file =>
                {
                    file.warning = "strict";
                    return true;
                });

            Assert.AreEqual(2, result.PendingPackages.Count);
            Assert.AreEqual(1, result.StaleInstallPaths.Count);
            Assert.AreEqual(2, result.PendingWarningInitTargets.Count);
            Assert.AreEqual(1, result.InstalledWarningCount);
            Assert.AreEqual(1, result.SingleFileWarningCount);
            Assert.AreEqual(0, result.StrictWarningCount);
            Assert.IsTrue(result.LoadMs >= 0);
            Assert.IsTrue(result.WarningInitMs >= 0);
            Assert.IsTrue(result.TotalMs >= 0);
            BMSPackage installedWarningPackage = result.PendingPackages.Single((BMSPackage pkg) => pkg.path.Equals(directoryPackagePath, StringComparison.OrdinalIgnoreCase));
            BMSPackage singleFileWarningPackage = result.PendingPackages.Single((BMSPackage pkg) => pkg.path.Equals(singleFileChartPath, StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.Warning_AlreadyInstalled, installedWarningPackage.BMSFiles[0].warning);
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.Warning_SingleBmsFile, singleFileWarningPackage.BMSFiles[0].warning);
        });
    }

    [TestMethod]
    public void LoadSongTable_DetectsLeapYearFolderTimestampInAnyLeapYear()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string folderPath = Path.Combine(lr2RootPath, "Songs", "LeapYearFolder");
            Directory.CreateDirectory(folderPath);
            Directory.SetLastWriteTime(folderPath, new DateTime(2024, 2, 29, 12, 0, 0));

            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.InsertOrReplace(new LR2SongDB.folder
                {
                    path = folderPath + Path.DirectorySeparatorChar,
                    title = "LeapYearFolder",
                    parent = "e2977170",
                    type = 1,
                    date = null,
                    adddate = 0
                }, typeof(LR2SongDB.folder));
            }

            RecordingDialogService dialogService = new RecordingDialogService
            {
                ResultToReturn = MessageBoxResult.Yes
            };
            RecordingFileMutationService fileMutationService = new RecordingFileMutationService();
            BmsLibraryInitializationService service = new BmsLibraryInitializationService();

            SongTableLoadResult result = service.LoadSongTable(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                dialogService,
                fileMutationService,
                null,
                ex => ex.Message);

            Assert.IsTrue(result.LeapYearDetected);
            Assert.AreEqual(1, fileMutationService.TimestampCalls.Count);
            Assert.AreEqual(folderPath, fileMutationService.TimestampCalls[0].Path);
            Assert.IsTrue(result.UpdatedFolders.Any((LR2SongDB.folder folder) => string.Equals(folder.path, folderPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(dialogService.Calls.Any((DialogCall call) => call.Button == MessageBoxButton.YesNo));
        });
    }

    [DataTestMethod]
    [DataRow(2023, 2, 28, 12, 0, 0)]
    [DataRow(2024, 3, 2, 0, 0, 0)]
    public void LoadSongTable_DoesNotTreatNonTargetTimestampAsLeapYearBug(int year, int month, int day, int hour, int minute, int second)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string folderPath = Path.Combine(lr2RootPath, "Songs", "NormalFolder");
            Directory.CreateDirectory(folderPath);
            Directory.SetLastWriteTime(folderPath, new DateTime(year, month, day, hour, minute, second));

            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.InsertOrReplace(new LR2SongDB.folder
                {
                    path = folderPath + Path.DirectorySeparatorChar,
                    title = "NormalFolder",
                    parent = "e2977170",
                    type = 1,
                    date = null,
                    adddate = 0
                }, typeof(LR2SongDB.folder));
            }

            RecordingDialogService dialogService = new RecordingDialogService
            {
                ResultToReturn = MessageBoxResult.Yes
            };
            RecordingFileMutationService fileMutationService = new RecordingFileMutationService();
            BmsLibraryInitializationService service = new BmsLibraryInitializationService();

            SongTableLoadResult result = service.LoadSongTable(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                dialogService,
                fileMutationService,
                null,
                ex => ex.Message);

            Assert.IsFalse(result.LeapYearDetected);
            Assert.AreEqual(0, fileMutationService.TimestampCalls.Count);
            Assert.AreEqual(0, result.UpdatedFolders.Count);
            Assert.IsFalse(dialogService.Calls.Any((DialogCall call) => call.Button == MessageBoxButton.YesNo));
        });
    }

    private static void WithTemporaryLr2SongDb(Action<string, string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_InitTests_" + Guid.NewGuid().ToString("N"));
        string lr2FilesPath = Path.Combine(tempRootPath, "LR2files");
        string databaseDirectoryPath = Path.Combine(lr2FilesPath, "Database");
        string songDbPath = Path.Combine(databaseDirectoryPath, "song.db");
        Directory.CreateDirectory(databaseDirectoryPath);
        File.WriteAllBytes(songDbPath, Array.Empty<byte>());
        try
        {
            testAction(tempRootPath, songDbPath);
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
            throw new NotSupportedException();
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
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

        public void DeleteFileShell(string filePath, Microsoft.VisualBasic.FileIO.UIOption uiOption, Microsoft.VisualBasic.FileIO.RecycleOption recycleOption, FileMutationOptions options = null!)
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

        public void DeleteDirectoryShell(string directoryPath, Microsoft.VisualBasic.FileIO.UIOption uiOption, Microsoft.VisualBasic.FileIO.RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            DeleteDirectoryDirect(directoryPath, recursive: true, options);
        }

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null!)
        {
        }
    }

    private sealed class RecordingFileMutationService : IFileMutationService
    {
        public List<TimestampCall> TimestampCalls { get; } = new List<TimestampCall>();

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

        public void DeleteFileDirect(string filePath, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void DeleteFileShell(string filePath, Microsoft.VisualBasic.FileIO.UIOption uiOption, Microsoft.VisualBasic.FileIO.RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void DeleteDirectoryShell(string directoryPath, Microsoft.VisualBasic.FileIO.UIOption uiOption, Microsoft.VisualBasic.FileIO.RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null!)
        {
            TimestampCalls.Add(new TimestampCall
            {
                Path = path,
                IsDirectory = isDirectory,
                CreationTime = creationTime,
                LastWriteTime = lastWriteTime
            });
        }
    }

    private sealed class TimestampCall
    {
        public string Path { get; set; } = string.Empty;

        public bool IsDirectory { get; set; }

        public DateTime? CreationTime { get; set; }

        public DateTime? LastWriteTime { get; set; }
    }

    private sealed class RecordingDialogService : IBmsLibraryDialogService
    {
        public List<DialogCall> Calls { get; } = new List<DialogCall>();

        public MessageBoxResult ResultToReturn { get; set; } = MessageBoxResult.OK;

        public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            Calls.Add(new DialogCall
            {
                Message = messageBoxText,
                Caption = caption,
                Button = button,
                Icon = icon,
                DefaultResult = defaultResult
            });
            return ResultToReturn;
        }
    }

    private sealed class DialogCall
    {
        public string Message { get; set; } = string.Empty;

        public string Caption { get; set; } = string.Empty;

        public MessageBoxButton Button { get; set; }

        public MessageBoxImage Icon { get; set; }

        public MessageBoxResult DefaultResult { get; set; }
    }
}
