using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.BmsLibraryInitializationTestSupport;
namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class BmsLibraryInitializationLr2NormalFolderTests
{
    [TestMethod]
    public void ApplyFileScanDiff_SyncsLr2NormalFoldersAfterFullScanWhenEnabled()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string packDirectoryPath = Path.Combine(lr2RootPath, "Pack");
            Directory.CreateDirectory(packDirectoryPath);
            string bmsPath = Path.Combine(packDirectoryPath, "added.bms");
            string folderInfoPath = Path.Combine(packDirectoryPath, "folderinfo.txt");
            File.WriteAllText(bmsPath, CreateValidBmsText("Folder Sync"), Encoding.ASCII);
            File.WriteAllText(folderInfoPath, "#TITLE Synced Folder", Encoding.GetEncoding(932));

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
            }

            ChartScanResult scanResult = CreateScanResult(
                [bmsPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { packDirectoryPath, Array.Empty<string>() }
                });
            scanResult.FolderInfoFilePaths.Add(folderInfoPath);

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = scanResult
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath]);

            Assert.IsTrue(result.Lr2NormalFolderSyncExecuted);
            Assert.IsFalse(result.Lr2NormalFolderSyncFailed);
            Assert.AreEqual(2, result.Lr2NormalFolderGeneratedCount);
            Assert.AreEqual(2, result.Lr2NormalFolderUpsertedCount);
            Assert.AreEqual(0, result.Lr2NormalFolderDeletedCount);
            Assert.AreEqual(2, result.Lr2NormalFolderMetadataRequestedDirectoryCount);
            Assert.AreEqual(2, result.Lr2NormalFolderMetadataResolvedDirectoryCount);
            Assert.AreEqual(1, result.Lr2NormalFolderInfoCandidateCount);
            Assert.AreEqual(1, result.Lr2NormalFolderInfoAppliedCount);

            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.folder root = verify.Table<LR2SongDB.folder>().Single(row => row.path == ToFolderPath(lr2RootPath));
            LR2SongDB.folder pack = verify.Table<LR2SongDB.folder>().Single(row => row.path == ToFolderPath(packDirectoryPath));
            Assert.AreEqual(1, root.type);
            Assert.AreEqual(1, pack.type);
            Assert.AreEqual("Synced Folder", pack.title);
            Assert.IsTrue(pack.date.HasValue);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_SyncsLr2NormalFoldersForChangedDirectoryMtimeWithoutSongDbDiff()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string packDirectoryPath = Path.Combine(lr2RootPath, "Pack");
            Directory.CreateDirectory(packDirectoryPath);
            string bmsPath = Path.Combine(packDirectoryPath, "current.bms");
            string folderInfoPath = Path.Combine(packDirectoryPath, "folderinfo.txt");
            File.WriteAllText(bmsPath, CreateValidBmsText("Current"), Encoding.ASCII);
            File.WriteAllText(folderInfoPath, "#TITLE Updated Folder", Encoding.GetEncoding(932));
            DateTime previousDirectoryTimestamp = new(2026, 6, 8, 1, 0, 0, DateTimeKind.Utc);
            DateTime currentDirectoryTimestamp = new(2026, 6, 8, 1, 5, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, previousDirectoryTimestamp);
            File.SetLastWriteTimeUtc(folderInfoPath, currentDirectoryTimestamp);
            Directory.SetLastWriteTimeUtc(lr2RootPath, previousDirectoryTimestamp);
            Directory.SetLastWriteTimeUtc(packDirectoryPath, currentDirectoryTimestamp);
            var currentFile = new TestableBmsFile
            {
                path = bmsPath,
                date = Lr2SongRowEnricher.ToLr2UnixSeconds(previousDirectoryTimestamp)
            };

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
                songDbConnection.InsertOrReplace(currentFile, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(lr2RootPath),
                    title = Path.GetFileName(lr2RootPath),
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(previousDirectoryTimestamp)
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(packDirectoryPath),
                    title = "Old Folder",
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(previousDirectoryTimestamp)
                }, typeof(LR2SongDB.folder));
            }

            ChartScanResult scanResult = CreateScanResult(
                [bmsPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { packDirectoryPath, Array.Empty<string>() }
                });
            scanResult.FolderInfoFilePaths.Add(folderInfoPath);
            scanResult.FolderInfoFileEntriesByPath[folderInfoPath] =
                new RootFileEnumerationEntry(folderInfoPath, File.GetLastWriteTimeUtc(folderInfoPath), new FileInfo(folderInfoPath).Length);

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [currentFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = scanResult
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath]);

            Assert.IsFalse(result.HasDbDiff);
            Assert.IsTrue(result.Lr2NormalFolderSyncExecuted);
            Assert.AreEqual(1, result.Lr2NormalFolderInfoCandidateCount);
            Assert.AreEqual(1, result.Lr2NormalFolderInfoAppliedCount);
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.folder pack = verify.Table<LR2SongDB.folder>().Single(row => row.path == ToFolderPath(packDirectoryPath));
            Assert.AreEqual("Updated Folder", pack.title);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_DoesNotSyncLr2NormalFoldersForUnchangedDirectoryMtimeWithoutSongDbDiff()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string packDirectoryPath = Path.Combine(lr2RootPath, "Pack");
            Directory.CreateDirectory(packDirectoryPath);
            string bmsPath = Path.Combine(packDirectoryPath, "current.bms");
            string folderInfoPath = Path.Combine(packDirectoryPath, "folderinfo.txt");
            File.WriteAllText(bmsPath, CreateValidBmsText("Current"), Encoding.ASCII);
            File.WriteAllText(folderInfoPath, "#TITLE Updated Folder", Encoding.GetEncoding(932));
            DateTime timestamp = new(2026, 6, 8, 1, 0, 0, DateTimeKind.Utc);
            DateTime folderInfoTimestamp = new(2026, 6, 8, 1, 5, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, timestamp);
            File.SetLastWriteTimeUtc(folderInfoPath, folderInfoTimestamp);
            Directory.SetLastWriteTimeUtc(packDirectoryPath, timestamp);
            Directory.SetLastWriteTimeUtc(lr2RootPath, timestamp);
            var currentFile = new TestableBmsFile
            {
                path = bmsPath,
                date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
            };
            currentFile.SetTextGroupFlagForTest(0);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
                songDbConnection.InsertOrReplace(currentFile, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(lr2RootPath),
                    title = Path.GetFileName(lr2RootPath),
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(packDirectoryPath),
                    title = "Old Folder",
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
                }, typeof(LR2SongDB.folder));
            }

            ChartScanResult scanResult = CreateScanResult(
                [bmsPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { packDirectoryPath, Array.Empty<string>() }
                });
            RootFileEnumerationEntry folderInfoEntry =
                new(folderInfoPath, File.GetLastWriteTimeUtc(folderInfoPath), new FileInfo(folderInfoPath).Length);
            scanResult.FolderInfoFilePaths.Add(folderInfoPath);
            scanResult.FolderInfoFileEntriesByPath[folderInfoPath] = folderInfoEntry;

            var logs = new List<string>();
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [currentFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = scanResult
                },
                0L,
                () => null,
                null,
                logInstallPerformance: logs.Add,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath]);

            Assert.IsFalse(result.HasDbDiff);
            Assert.IsFalse(result.Lr2NormalFolderSyncExecuted);
            Assert.IsTrue(logs.Any(message => message.Contains("lr2_normal_folder_sync skipped reason=no_db_diff")));
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.folder pack = verify.Table<LR2SongDB.folder>().Single(row => row.path == ToFolderPath(packDirectoryPath));
            Assert.AreEqual("Old Folder", pack.title);
        });
    }

    [TestMethod]
    public void LoadNormalFolderMtimeSnapshot_LoadsOnlyNormalFolderRowsUnderRoots()
    {
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string packDirectoryPath = Path.Combine(lr2RootPath, "Pack");
            string appManagedLr2FolderPath = Path.Combine(lr2RootPath, "LR2files", "CustomFolder", "Table.lr2folder");
            string outsideFolderPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_Outside_" + Guid.NewGuid().ToString("N"));
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.folder>();
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(lr2RootPath),
                    title = Path.GetFileName(lr2RootPath),
                    type = 1,
                    date = 1
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(packDirectoryPath),
                    title = "Pack",
                    type = 1,
                    date = 2
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = appManagedLr2FolderPath,
                    title = "Table",
                    type = 2,
                    date = 3
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(outsideFolderPath),
                    title = "Outside",
                    type = 1,
                    date = 4
                }, typeof(LR2SongDB.folder));
            }

            var logs = new List<string>();
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            Lr2NormalFolderMtimeSnapshot snapshot = service.LoadNormalFolderMtimeSnapshot(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [lr2RootPath],
                logs.Add);

            Assert.IsNotNull(snapshot);
            Assert.IsTrue(snapshot.ExistingRowsByPath.ContainsKey(ToFolderPath(lr2RootPath)));
            Assert.IsTrue(snapshot.ExistingRowsByPath.ContainsKey(ToFolderPath(packDirectoryPath)));
            Assert.IsFalse(snapshot.ExistingRowsByPath.ContainsKey(appManagedLr2FolderPath));
            Assert.IsFalse(snapshot.ExistingRowsByPath.ContainsKey(ToFolderPath(outsideFolderPath)));
            Assert.IsTrue(logs.Any(message => message.Contains("lr2_normal_folder_mtime_snapshot_prefetch")
                && message.Contains("existingRows=2")));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_UsesPrefetchedNormalFolderMtimeSnapshot()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string packDirectoryPath = Path.Combine(lr2RootPath, "Pack");
            Directory.CreateDirectory(packDirectoryPath);
            string bmsPath = Path.Combine(packDirectoryPath, "current.bms");
            string folderInfoPath = Path.Combine(packDirectoryPath, "folderinfo.txt");
            File.WriteAllText(bmsPath, CreateValidBmsText("Current"), Encoding.ASCII);
            File.WriteAllText(folderInfoPath, "#TITLE Updated Folder", Encoding.GetEncoding(932));
            DateTime timestamp = new(2026, 6, 8, 1, 0, 0, DateTimeKind.Utc);
            DateTime folderInfoTimestamp = new(2026, 6, 8, 1, 5, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, timestamp);
            File.SetLastWriteTimeUtc(folderInfoPath, folderInfoTimestamp);
            Directory.SetLastWriteTimeUtc(packDirectoryPath, timestamp);
            Directory.SetLastWriteTimeUtc(lr2RootPath, timestamp);
            var currentFile = new TestableBmsFile
            {
                path = bmsPath,
                date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
            };
            currentFile.SetTextGroupFlagForTest(0);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
                songDbConnection.InsertOrReplace(currentFile, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(lr2RootPath),
                    title = Path.GetFileName(lr2RootPath),
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(packDirectoryPath),
                    title = "Old Folder",
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
                }, typeof(LR2SongDB.folder));
            }

            ChartScanResult scanResult = CreateScanResult(
                [bmsPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { packDirectoryPath, Array.Empty<string>() }
                });
            RootFileEnumerationEntry folderInfoEntry =
                new(folderInfoPath, File.GetLastWriteTimeUtc(folderInfoPath), new FileInfo(folderInfoPath).Length);
            scanResult.FolderInfoFilePaths.Add(folderInfoPath);
            scanResult.FolderInfoFileEntriesByPath[folderInfoPath] = folderInfoEntry;

            var logs = new List<string>();
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            Lr2NormalFolderMtimeSnapshot snapshot = service.LoadNormalFolderMtimeSnapshot(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [lr2RootPath]);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [currentFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = scanResult
                },
                0L,
                () => null,
                null,
                logInstallPerformance: logs.Add,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath],
                normalFolderMtimeSnapshot: snapshot);

            Assert.IsFalse(result.HasDbDiff);
            Assert.IsFalse(result.Lr2NormalFolderSyncExecuted);
            Assert.IsTrue(logs.Any(message => message.Contains("lr2_normal_folder_mtime_diff")
                && message.Contains("prefetched=true")));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_DoesNotSyncLr2NormalFoldersWhenOnlyFolderInfoMetadataDiffers()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string packDirectoryPath = Path.Combine(lr2RootPath, "Pack");
            Directory.CreateDirectory(packDirectoryPath);
            string bmsPath = Path.Combine(packDirectoryPath, "current.bms");
            string folderInfoPath = Path.Combine(packDirectoryPath, "folderinfo.txt");
            File.WriteAllText(bmsPath, CreateValidBmsText("Current"), Encoding.ASCII);
            File.WriteAllText(folderInfoPath, "#TITLE Updated Folder", Encoding.GetEncoding(932));
            DateTime timestamp = new(2026, 6, 8, 1, 0, 0, DateTimeKind.Utc);
            DateTime folderInfoTimestamp = new(2026, 6, 8, 1, 5, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, timestamp);
            File.SetLastWriteTimeUtc(folderInfoPath, folderInfoTimestamp);
            Directory.SetLastWriteTimeUtc(lr2RootPath, timestamp);
            Directory.SetLastWriteTimeUtc(packDirectoryPath, timestamp);
            var currentFile = new TestableBmsFile
            {
                path = bmsPath,
                date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
            };
            currentFile.SetTextGroupFlagForTest(0);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
                songDbConnection.InsertOrReplace(currentFile, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(lr2RootPath),
                    title = Path.GetFileName(lr2RootPath),
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(packDirectoryPath),
                    title = "Old Folder",
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
                }, typeof(LR2SongDB.folder));
            }

            ChartScanResult scanResult = CreateScanResult(
                [bmsPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { packDirectoryPath, Array.Empty<string>() }
                });
            scanResult.FolderInfoFilePaths.Add(folderInfoPath);
            scanResult.FolderInfoFileEntriesByPath[folderInfoPath] =
                new RootFileEnumerationEntry(folderInfoPath, folderInfoTimestamp, new FileInfo(folderInfoPath).Length);

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [currentFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = scanResult
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath]);

            Assert.IsFalse(result.HasDbDiff);
            Assert.IsFalse(result.Lr2NormalFolderSyncExecuted);
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.folder pack = verify.Table<LR2SongDB.folder>().Single(row => row.path == ToFolderPath(packDirectoryPath));
            Assert.AreEqual("Old Folder", pack.title);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_SyncsLr2NormalFoldersForChangedDirectoryMtimeAndDeletedFolderInfoWithoutSongDbDiff()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string packDirectoryPath = Path.Combine(lr2RootPath, "Pack");
            Directory.CreateDirectory(packDirectoryPath);
            string bmsPath = Path.Combine(packDirectoryPath, "current.bms");
            string folderInfoPath = Path.Combine(packDirectoryPath, "folderinfo.txt");
            File.WriteAllText(bmsPath, CreateValidBmsText("Current"), Encoding.ASCII);
            DateTime previousDirectoryTimestamp = new(2026, 6, 8, 1, 0, 0, DateTimeKind.Utc);
            DateTime currentDirectoryTimestamp = new(2026, 6, 8, 1, 5, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, previousDirectoryTimestamp);
            Directory.SetLastWriteTimeUtc(lr2RootPath, previousDirectoryTimestamp);
            Directory.SetLastWriteTimeUtc(packDirectoryPath, currentDirectoryTimestamp);
            var currentFile = new TestableBmsFile
            {
                path = bmsPath,
                date = Lr2SongRowEnricher.ToLr2UnixSeconds(previousDirectoryTimestamp)
            };
            currentFile.SetTextGroupFlagForTest(0);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
                songDbConnection.InsertOrReplace(currentFile, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(lr2RootPath),
                    title = Path.GetFileName(lr2RootPath),
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(previousDirectoryTimestamp)
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(packDirectoryPath),
                    title = "Old FolderInfo",
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(previousDirectoryTimestamp)
                }, typeof(LR2SongDB.folder));
            }

            ChartScanResult scanResult = CreateScanResult(
                [bmsPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { packDirectoryPath, Array.Empty<string>() }
                });

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [currentFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = scanResult
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath]);

            Assert.IsFalse(result.HasDbDiff);
            Assert.IsTrue(result.Lr2NormalFolderSyncExecuted);
            Assert.AreEqual(0, result.Lr2NormalFolderInfoCandidateCount);
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.folder pack = verify.Table<LR2SongDB.folder>().Single(row => row.path == ToFolderPath(packDirectoryPath));
            Assert.AreEqual("Pack", pack.title);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_UsesDirectoryMtimeFromScanSurfaceForLr2NormalFolders()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string packDirectoryPath = Path.Combine(lr2RootPath, "Pack");
            Directory.CreateDirectory(packDirectoryPath);
            string bmsPath = Path.Combine(packDirectoryPath, "added.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Folder Surface Date"), Encoding.ASCII);
            DateTime rootSurfaceTimestamp = new(2026, 6, 7, 1, 0, 0, DateTimeKind.Utc);
            DateTime packSurfaceTimestamp = new(2026, 6, 7, 2, 0, 0, DateTimeKind.Utc);
            DateTime liveTimestamp = new(2026, 6, 8, 1, 0, 0, DateTimeKind.Utc);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
            }

            ChartScanResult scanResult = CreateScanResult(
                [bmsPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { packDirectoryPath, Array.Empty<string>() }
                });
            scanResult.DirectoryEntriesByPath[Lr2FolderPath.NormalizeDirectoryPath(lr2RootPath)] =
                new RootFileEnumerationEntry(lr2RootPath, rootSurfaceTimestamp);
            scanResult.DirectoryEntriesByPath[Lr2FolderPath.NormalizeDirectoryPath(packDirectoryPath)] =
                new RootFileEnumerationEntry(packDirectoryPath, packSurfaceTimestamp);
            Directory.SetLastWriteTimeUtc(lr2RootPath, liveTimestamp);
            Directory.SetLastWriteTimeUtc(packDirectoryPath, liveTimestamp);

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = scanResult
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath]);

            Assert.IsTrue(result.Lr2NormalFolderSyncExecuted);
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.folder root = verify.Table<LR2SongDB.folder>().Single(row => row.path == ToFolderPath(lr2RootPath));
            LR2SongDB.folder pack = verify.Table<LR2SongDB.folder>().Single(row => row.path == ToFolderPath(packDirectoryPath));
            Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(rootSurfaceTimestamp), root.date);
            Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(packSurfaceTimestamp), pack.date);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_PopulatesLr2FolderSurfaceFromProducerDiscoveryRoots()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsRootPath = Path.Combine(lr2RootPath, "Songs");
            string normalOutputBasePath = Path.Combine(lr2RootPath, "CustomFolderOutput");
            string rootOutputBasePath = Path.Combine(lr2RootPath, "RootCustomFolderOutput");
            string builtinCustomFolderPath = Path.Combine(lr2RootPath, "LR2files", "CustomFolder");
            Directory.CreateDirectory(bmsRootPath);
            Directory.CreateDirectory(normalOutputBasePath);
            Directory.CreateDirectory(rootOutputBasePath);
            Directory.CreateDirectory(builtinCustomFolderPath);

            string rootLr2FolderPath = Path.Combine(bmsRootPath, "external.lr2folder");
            string outputLr2FolderPath = Path.Combine(normalOutputBasePath, "0000.lr2folder");
            string rootOutputLr2FolderPath = Path.Combine(rootOutputBasePath, "root.lr2folder");
            string builtinFavoritePath = Path.Combine(builtinCustomFolderPath, "favorite.lr2folder");
            string builtinNewsongPath = Path.Combine(builtinCustomFolderPath, "newsong.lr2folder");
            File.WriteAllText(rootLr2FolderPath, "#TITLE External", Encoding.GetEncoding(932));
            File.WriteAllText(outputLr2FolderPath, "#TITLE Output", Encoding.GetEncoding(932));
            File.WriteAllText(rootOutputLr2FolderPath, "#TITLE Root Output", Encoding.GetEncoding(932));
            File.WriteAllText(builtinFavoritePath, "#TITLE Favorite", Encoding.GetEncoding(932));
            File.WriteAllText(builtinNewsongPath, "#TITLE Newsong", Encoding.GetEncoding(932));

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
            }

            var events = new List<string>();
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                    LR2RootPath = lr2RootPath,
                    LR2CustomFolderOutputBaseDir = normalOutputBasePath,
                    LR2CustomFolderOutputBaseDirRootType = rootOutputBasePath
                },
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                },
                0L,
                () => null,
                null,
                logEverythingScan: message =>
                {
                    if (message.StartsWith("lr2folder_scan", StringComparison.OrdinalIgnoreCase))
                    {
                        events.Add(message);
                    }
                },
                currentBmsonSongs: [],
                scanCompleted: () => events.Add("scanCompleted"),
                fileDiffStarted: () => events.Add("fileDiffStarted"),
                lr2NormalFolderSyncRootDirectories: [bmsRootPath],
                lr2FolderDiscoveryRootDirectories: [bmsRootPath],
                lr2BuiltinCustomFolderSettings: new Lr2BuiltinCustomFolderSettings(0x2, 24, includeNewSongFolder: false));

            Assert.IsTrue(result.Lr2ScanSurfaceAvailable);
            CollectionAssert.Contains(result.Lr2ScanLr2FolderDiscoveryDirectories.ToList(), bmsRootPath);
            CollectionAssert.Contains(result.Lr2ScanLr2FolderDiscoveryDirectories.ToList(), normalOutputBasePath);
            CollectionAssert.Contains(result.Lr2ScanLr2FolderDiscoveryDirectories.ToList(), rootOutputBasePath);
            CollectionAssert.Contains(result.Lr2ScanLr2FolderDiscoveryDirectories.ToList(), builtinCustomFolderPath);
            CollectionAssert.Contains(result.Lr2ScanLr2FolderFilePaths.ToList(), rootLr2FolderPath);
            CollectionAssert.Contains(result.Lr2ScanLr2FolderFilePaths.ToList(), outputLr2FolderPath);
            CollectionAssert.Contains(result.Lr2ScanLr2FolderFilePaths.ToList(), rootOutputLr2FolderPath);
            CollectionAssert.Contains(result.Lr2ScanLr2FolderFilePaths.ToList(), builtinFavoritePath);
            CollectionAssert.DoesNotContain(result.Lr2ScanLr2FolderFilePaths.ToList(), builtinNewsongPath);
            Assert.IsTrue(result.Lr2ScanLr2FolderFileEntries.TryGetValue(builtinFavoritePath, out RootFileEnumerationEntry builtinEntry));
            Assert.IsTrue(builtinEntry.LastWriteTimeUtc.HasValue);
            Assert.IsTrue(result.Lr2ScanLr2FolderFileDiscoveryComplete);
            int lr2FolderScanIndex = events.FindIndex(message => message.StartsWith("lr2folder_scan success", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(lr2FolderScanIndex >= 0);
            Assert.IsTrue(lr2FolderScanIndex < events.IndexOf("scanCompleted"));
            Assert.IsTrue(events.IndexOf("scanCompleted") < events.IndexOf("fileDiffStarted"));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_SyncsLr2NormalFoldersOnlyForAffectedDeletedBmsScope()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string keepDirectoryPath = Path.Combine(lr2RootPath, "Keep");
            string removedDirectoryPath = Path.Combine(lr2RootPath, "Removed");
            string staleOtherDirectoryPath = Path.Combine(lr2RootPath, "OtherStale");
            Directory.CreateDirectory(keepDirectoryPath);
            Directory.CreateDirectory(removedDirectoryPath);
            string keepPath = Path.Combine(keepDirectoryPath, "keep.bms");
            string removedPath = Path.Combine(removedDirectoryPath, "removed.bms");
            File.WriteAllText(keepPath, CreateValidBmsText("Keep"), Encoding.ASCII);
            File.WriteAllText(removedPath, CreateValidBmsText("Removed"), Encoding.ASCII);
            var keepTimestamp = new DateTime(2026, 6, 6, 1, 0, 0, DateTimeKind.Utc);
            var removedTimestamp = new DateTime(2026, 6, 6, 2, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(keepPath, keepTimestamp);
            File.SetLastWriteTimeUtc(removedPath, removedTimestamp);
            var keepFile = new TestableBmsFile
            {
                path = keepPath,
                date = Lr2SongRowEnricher.ToLr2UnixSeconds(keepTimestamp)
            };
            keepFile.SetTextGroupFlagForTest(0);
            keepFile.SetHash(BMSFile.CreateBMSFileFromFile(keepPath).hash);
            var removedFile = new TestableBmsFile
            {
                path = removedPath,
                date = Lr2SongRowEnricher.ToLr2UnixSeconds(removedTimestamp)
            };
            removedFile.SetHash(BMSFile.CreateBMSFileFromFile(removedPath).hash);
            string removedFolderPath = ToFolderPath(removedDirectoryPath);
            string staleOtherFolderPath = ToFolderPath(staleOtherDirectoryPath);
            var logs = new List<string>();

            File.Delete(removedPath);
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
                songDbConnection.InsertOrReplace(keepFile, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(removedFile, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(lr2RootPath),
                    type = 1
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = removedFolderPath,
                    type = 1
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = staleOtherFolderPath,
                    type = 1
                }, typeof(LR2SongDB.folder));
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [keepFile, removedFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [keepPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { keepDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                logInstallPerformance: logs.Add,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath]);

            Assert.IsTrue(result.HasDbDiff);
            Assert.IsTrue(result.Lr2NormalFolderSyncExecuted);
            Assert.AreEqual(1, result.Lr2NormalFolderDeletedCount);
            Assert.IsTrue(logs.Any(message => message.Contains("lr2_normal_folder_sync done")
                && message.Contains("paths=0")
                && message.Contains("pruneScopes=1")));

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", removedFolderPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", staleOtherFolderPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_DeletedNestedBmsDoesNotExpandLr2NormalFolderScopeToRootChild()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string keepDirectoryPath = Path.Combine(lr2RootPath, "Big", "Keep");
            string removedDirectoryPath = Path.Combine(lr2RootPath, "Big", "Removed");
            string staleSiblingDirectoryPath = Path.Combine(lr2RootPath, "Big", "SiblingStale");
            Directory.CreateDirectory(keepDirectoryPath);
            Directory.CreateDirectory(removedDirectoryPath);
            string keepPath = Path.Combine(keepDirectoryPath, "keep.bms");
            string removedPath = Path.Combine(removedDirectoryPath, "removed.bms");
            File.WriteAllText(keepPath, CreateValidBmsText("Keep Nested"), Encoding.ASCII);
            File.WriteAllText(removedPath, CreateValidBmsText("Removed Nested"), Encoding.ASCII);
            var keepFile = new TestableBmsFile
            {
                path = keepPath,
                date = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(keepPath))
            };
            keepFile.SetHash(BMSFile.CreateBMSFileFromFile(keepPath).hash);
            var removedFile = new TestableBmsFile
            {
                path = removedPath,
                date = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(removedPath))
            };
            removedFile.SetHash(BMSFile.CreateBMSFileFromFile(removedPath).hash);
            string bigFolderPath = ToFolderPath(Path.Combine(lr2RootPath, "Big"));
            string removedFolderPath = ToFolderPath(removedDirectoryPath);
            string staleSiblingFolderPath = ToFolderPath(staleSiblingDirectoryPath);
            var logs = new List<string>();

            File.Delete(removedPath);
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
                songDbConnection.InsertOrReplace(keepFile, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(removedFile, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(lr2RootPath),
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(lr2RootPath))
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = bigFolderPath,
                    type = 1
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = removedFolderPath,
                    type = 1
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = staleSiblingFolderPath,
                    type = 1
                }, typeof(LR2SongDB.folder));
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [keepFile, removedFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [keepPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { keepDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                logInstallPerformance: logs.Add,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath]);

            Assert.IsTrue(result.HasDbDiff);
            Assert.IsTrue(result.Lr2NormalFolderSyncExecuted);
            Assert.AreEqual(1, result.Lr2NormalFolderDeletedCount);
            Assert.IsTrue(logs.Any(message => message.Contains("lr2_normal_folder_sync done")
                && message.Contains("paths=0")
                && message.Contains("pruneScopes=1")));

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", removedFolderPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", bigFolderPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", staleSiblingFolderPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_DeletedLastNestedBmsPrunesEmptyAncestorFolder()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string packDirectoryPath = Path.Combine(lr2RootPath, "EmptyPack");
            string removedDirectoryPath = Path.Combine(packDirectoryPath, "Removed");
            Directory.CreateDirectory(removedDirectoryPath);
            string removedPath = Path.Combine(removedDirectoryPath, "removed.bms");
            File.WriteAllText(removedPath, CreateValidBmsText("Removed Last Nested"), Encoding.ASCII);
            var removedFile = new TestableBmsFile
            {
                path = removedPath,
                date = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(removedPath))
            };
            removedFile.SetHash(BMSFile.CreateBMSFileFromFile(removedPath).hash);
            string packFolderPath = ToFolderPath(packDirectoryPath);
            string removedFolderPath = ToFolderPath(removedDirectoryPath);

            File.Delete(removedPath);
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
                songDbConnection.InsertOrReplace(removedFile, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(lr2RootPath),
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(lr2RootPath))
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = packFolderPath,
                    type = 1
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = removedFolderPath,
                    type = 1
                }, typeof(LR2SongDB.folder));
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [removedFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase),
                        [lr2RootPath])
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath]);

            Assert.IsTrue(result.HasDbDiff);
            Assert.IsTrue(result.Lr2NormalFolderSyncExecuted);
            Assert.AreEqual(2, result.Lr2NormalFolderDeletedCount);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", removedFolderPath));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", packFolderPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", ToFolderPath(lr2RootPath)));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_DeletedRootLevelBmsDoesNotExpandLr2NormalFolderScopeToRoot()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string keepDirectoryPath = Path.Combine(lr2RootPath, "Keep");
            string staleDirectoryPath = Path.Combine(lr2RootPath, "StaleSibling");
            Directory.CreateDirectory(keepDirectoryPath);
            string keepPath = Path.Combine(keepDirectoryPath, "keep.bms");
            string removedPath = Path.Combine(lr2RootPath, "removed-root.bms");
            File.WriteAllText(keepPath, CreateValidBmsText("Keep Root Sibling"), Encoding.ASCII);
            File.WriteAllText(removedPath, CreateValidBmsText("Removed Root Level"), Encoding.ASCII);
            var keepFile = new TestableBmsFile
            {
                path = keepPath,
                date = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(keepPath))
            };
            keepFile.SetHash(BMSFile.CreateBMSFileFromFile(keepPath).hash);
            var removedFile = new TestableBmsFile
            {
                path = removedPath,
                date = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(removedPath))
            };
            removedFile.SetHash(BMSFile.CreateBMSFileFromFile(removedPath).hash);
            string staleFolderPath = ToFolderPath(staleDirectoryPath);
            var logs = new List<string>();

            File.Delete(removedPath);
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
                songDbConnection.InsertOrReplace(keepFile, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(removedFile, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(lr2RootPath),
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(lr2RootPath))
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(keepDirectoryPath),
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(keepDirectoryPath))
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = staleFolderPath,
                    type = 1
                }, typeof(LR2SongDB.folder));
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [keepFile, removedFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [keepPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { keepDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                logInstallPerformance: logs.Add,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath]);

            Assert.IsTrue(result.HasDbDiff);
            Assert.IsFalse(result.Lr2NormalFolderSyncExecuted);
            Assert.AreEqual(0, result.Lr2NormalFolderDeletedCount);
            Assert.IsTrue(logs.Any(message => message.Contains("lr2_normal_folder_sync skipped reason=no_normal_folder_mtime_diff")));

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", staleFolderPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_DoesNotSyncStaleLr2NormalFoldersOutsideLr2Mode()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string packDirectoryPath = Path.Combine(lr2RootPath, "Pack");
            Directory.CreateDirectory(packDirectoryPath);
            string bmsPath = Path.Combine(packDirectoryPath, "added.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Folder Sync Disabled"), Encoding.ASCII);
            string stalePath = ToFolderPath(Path.Combine(lr2RootPath, "Stale"));

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = stalePath,
                    type = 1
                }, typeof(LR2SongDB.folder));
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false,
                },
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { packDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath]);

            Assert.IsFalse(result.Lr2NormalFolderSyncExecuted);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", stalePath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_DoesNotSyncLr2NormalFoldersOutsideLr2Mode()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string packDirectoryPath = Path.Combine(lr2RootPath, "Pack");
            Directory.CreateDirectory(packDirectoryPath);
            string bmsPath = Path.Combine(packDirectoryPath, "added.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Folder Sync Disabled"), Encoding.ASCII);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false,
                },
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { packDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath]);

            Assert.IsFalse(result.Lr2NormalFolderSyncExecuted);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder;"));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_DoesNotSyncLr2NormalFoldersWhenScanIsIncomplete()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string packDirectoryPath = Path.Combine(lr2RootPath, "Pack");
            Directory.CreateDirectory(packDirectoryPath);
            string bmsPath = Path.Combine(packDirectoryPath, "added.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Folder Sync Incomplete"), Encoding.ASCII);
            string stalePath = ToFolderPath(Path.Combine(lr2RootPath, "Stale"));
            var logs = new List<string>();

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = stalePath,
                    type = 1
                }, typeof(LR2SongDB.folder));
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [],
                new ChartScanExecutionResult
                {
                    Success = false,
                    IsComplete = false,
                    IncompleteReason = "directory_enumeration_failed:" + lr2RootPath,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { packDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                logInstallPerformance: logs.Add,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath]);

            Assert.IsFalse(result.Lr2NormalFolderSyncExecuted);
            Assert.IsFalse(result.HasDbDiff);
            Assert.AreEqual("directory_enumeration_failed:" + lr2RootPath, result.ScanFallbackReason);
            Assert.IsFalse(logs.Any(message => message.Contains("lr2_normal_folder_sync")));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", stalePath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_DoesNotSyncLr2NormalFoldersWhenThereIsNoDbDiff()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string packDirectoryPath = Path.Combine(lr2RootPath, "Pack");
            Directory.CreateDirectory(packDirectoryPath);
            string bmsPath = Path.Combine(packDirectoryPath, "current.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Current"), Encoding.ASCII);
            DateTime timestamp = new DateTime(2026, 6, 6, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, timestamp);
            Directory.SetLastWriteTimeUtc(packDirectoryPath, timestamp);
            Directory.SetLastWriteTimeUtc(lr2RootPath, timestamp);
            var currentFile = new TestableBmsFile
            {
                path = bmsPath,
                date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
            };
            currentFile.SetTextGroupFlagForTest(0);
            string stalePath = ToFolderPath(Path.Combine(lr2RootPath, "Stale"));
            var logs = new List<string>();

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
                songDbConnection.InsertOrReplace(currentFile, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(lr2RootPath),
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(packDirectoryPath),
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = stalePath,
                    type = 1
                }, typeof(LR2SongDB.folder));
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [currentFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { packDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                logInstallPerformance: logs.Add,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath]);

            Assert.IsFalse(result.HasDbDiff);
            Assert.IsFalse(result.Lr2NormalFolderSyncExecuted);
            Assert.IsTrue(logs.Any(message => message.Contains("lr2_normal_folder_sync skipped reason=no_db_diff")));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(3L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", stalePath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_DoesNotSyncLr2NormalFoldersForBmsTextOnlyDiff()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "TextOnly");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "current.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Text Only"), Encoding.ASCII);
            DateTime timestamp = new DateTime(2026, 6, 6, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, timestamp);
            Directory.SetLastWriteTimeUtc(chartDirectoryPath, timestamp);
            Directory.SetLastWriteTimeUtc(lr2RootPath, timestamp);
            BMSFile parsed = BMSFile.CreateBMSFileFromFile(bmsPath);
            var currentFile = new TestableBmsFile
            {
                path = bmsPath,
                date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
            };
            currentFile.SetHash(parsed.hash);
            currentFile.SetTextGroupFlagForTest(0);
            string stalePath = ToFolderPath(Path.Combine(lr2RootPath, "Stale"));
            var logs = new List<string>();

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
                songDbConnection.InsertOrReplace(currentFile, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(lr2RootPath),
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(chartDirectoryPath),
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = stalePath,
                    type = 1
                }, typeof(LR2SongDB.folder));
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [currentFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, ["readme.txt"] }
                        })
                },
                0L,
                () => null,
                null,
                logInstallPerformance: logs.Add,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath]);

            Assert.IsTrue(result.HasDbDiff);
            Assert.AreEqual(1, result.BmsTextOnlyUpdateCount);
            Assert.IsFalse(result.Lr2NormalFolderSyncExecuted);
            Assert.IsTrue(logs.Any(message => message.Contains("lr2_normal_folder_sync skipped reason=no_normal_folder_mtime_diff")));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", stalePath));
        });
    }

}
