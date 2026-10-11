using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.BmsLibraryInitializationTestSupport;
namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class BmsLibraryInitializationLr2NormalFolderTests
{
    /// <summary>
    /// 完全scanでLR2通常folderを同期し、folderinfoのtitleと実folder日時をDBへ保存する。
    /// </summary>
    [TestMethod]
    public void ApplyFileScanDiff_SyncsLr2NormalFoldersAfterFullScanWhenEnabled()
    {

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
                songDbConnection.RunInTransaction(() =>
                {
                    songDbConnection.CreateTable<LR2SongDB.song>();
                    songDbConnection.CreateTable<LR2SongDB.folder>();
                });
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

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder root = verify.Table<LR2SongDB.folder>().Single(row => row.path == ToFolderPath(lr2RootPath));
            LR2SongDB.folder pack = verify.Table<LR2SongDB.folder>().Single(row => row.path == ToFolderPath(packDirectoryPath));
            Assert.AreEqual(1, root.type);
            Assert.AreEqual(1, pack.type);
            Assert.AreEqual("Synced Folder", pack.title);
            Assert.IsTrue(pack.date.HasValue);
        });
    }

    /// <summary>
    /// song差分がなくてもdirectory日時が変わると通常folderを同期し、folderinfoの新しいtitleを保存する。
    /// </summary>
    [TestMethod]
    public void ApplyFileScanDiff_SyncsLr2NormalFoldersForChangedDirectoryMtimeWithoutSongDbDiff()
    {

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
            ChartFile currentFile = ChartTestValues.Empty() with
            {
                Path = bmsPath,
                Date = Lr2SongRowEnricher.ToLr2UnixSeconds(previousDirectoryTimestamp)
            };

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.RunInTransaction(() =>
                {
                    songDbConnection.CreateTable<LR2SongDB.song>();
                    songDbConnection.CreateTable<LR2SongDB.folder>();
                    songDbConnection.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(currentFile), typeof(LR2SongDB.song));
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
                });
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
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder pack = verify.Table<LR2SongDB.folder>().Single(row => row.path == ToFolderPath(packDirectoryPath));
            Assert.AreEqual("Updated Folder", pack.title);
        });
    }

    /// <summary>
    /// directory日時が同じならfolderinfoだけ新しくても同期せず、旧titleを保存したまま保つ。
    /// </summary>
    [TestMethod]
    public void ApplyFileScanDiff_DoesNotSyncLr2NormalFoldersForUnchangedDirectoryMtimeWithoutSongDbDiff()
    {

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
            ChartFile currentFile = ChartTestValues.Empty() with
            {
                Path = bmsPath,
                Date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
            };
            currentFile = currentFile with { Txt = 0 };

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.RunInTransaction(() =>
                {
                    songDbConnection.CreateTable<LR2SongDB.song>();
                    songDbConnection.CreateTable<LR2SongDB.folder>();
                    songDbConnection.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(currentFile), typeof(LR2SongDB.song));
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
                });
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
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder pack = verify.Table<LR2SongDB.folder>().Single(row => row.path == ToFolderPath(packDirectoryPath));
            Assert.AreEqual("Old Folder", pack.title);
        });
    }

    /// <summary>
    /// 指定root内の通常folderだけを日時snapshotへ取得し、管理表folderとroot外の行を混ぜない。
    /// </summary>
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
                songDbConnection.RunInTransaction(() =>
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
                });
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

    /// <summary>
    /// 事前取得した通常folder日時snapshotを差分判定へ接続し、同じdirectoryの再同期を起こさない。
    /// </summary>
    [TestMethod]
    public void ApplyFileScanDiff_UsesPrefetchedNormalFolderMtimeSnapshot()
    {

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
            ChartFile currentFile = ChartTestValues.Empty() with
            {
                Path = bmsPath,
                Date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
            };
            currentFile = currentFile with { Txt = 0 };

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.RunInTransaction(() =>
                {
                    songDbConnection.CreateTable<LR2SongDB.song>();
                    songDbConnection.CreateTable<LR2SongDB.folder>();
                    songDbConnection.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(currentFile), typeof(LR2SongDB.song));
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
                });
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

    /// <summary>
    /// directory日時の変更とfolderinfoの削除を同期し、旧metadataのtitleを実directory名へ戻す。
    /// </summary>
    [TestMethod]
    public void ApplyFileScanDiff_SyncsLr2NormalFoldersForChangedDirectoryMtimeAndDeletedFolderInfoWithoutSongDbDiff()
    {

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
            ChartFile currentFile = ChartTestValues.Empty() with
            {
                Path = bmsPath,
                Date = Lr2SongRowEnricher.ToLr2UnixSeconds(previousDirectoryTimestamp)
            };
            currentFile = currentFile with { Txt = 0 };

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.RunInTransaction(() =>
                {
                    songDbConnection.CreateTable<LR2SongDB.song>();
                    songDbConnection.CreateTable<LR2SongDB.folder>();
                    songDbConnection.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(currentFile), typeof(LR2SongDB.song));
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
                });
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
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder pack = verify.Table<LR2SongDB.folder>().Single(row => row.path == ToFolderPath(packDirectoryPath));
            Assert.AreEqual("Pack", pack.title);
        });
    }

    /// <summary>
    /// liveのdirectory日時が変わっていてもscanで捕捉した日時を保存し、同じscan結果へ後続の変化を混ぜない。
    /// </summary>
    [TestMethod]
    public void ApplyFileScanDiff_UsesDirectoryMtimeFromScanSurfaceForLr2NormalFolders()
    {

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
                songDbConnection.RunInTransaction(() =>
                {
                    songDbConnection.CreateTable<LR2SongDB.song>();
                    songDbConnection.CreateTable<LR2SongDB.folder>();
                });
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
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder root = verify.Table<LR2SongDB.folder>().Single(row => row.path == ToFolderPath(lr2RootPath));
            LR2SongDB.folder pack = verify.Table<LR2SongDB.folder>().Single(row => row.path == ToFolderPath(packDirectoryPath));
            Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(rootSurfaceTimestamp), root.date);
            Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(packSurfaceTimestamp), pack.date);
        });
    }

    /// <summary>
    /// BMS削除が影響するdirectoryの通常folderだけを退役し、別の古いfolder行を巻き込まない。
    /// </summary>
    [TestMethod]
    public void ApplyFileScanDiff_SyncsLr2NormalFoldersOnlyForAffectedDeletedBmsScope()
    {

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
            ChartFile keepFile = ChartTestValues.Empty() with
            {
                Path = keepPath,
                Date = Lr2SongRowEnricher.ToLr2UnixSeconds(keepTimestamp)
            };
            keepFile = keepFile with { Txt = 0 };
            keepFile = keepFile with { Md5 = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(keepPath)).Md5 };
            ChartFile removedFile = ChartTestValues.Empty() with
            {
                Path = removedPath,
                Date = Lr2SongRowEnricher.ToLr2UnixSeconds(removedTimestamp)
            };
            removedFile = removedFile with { Md5 = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(removedPath)).Md5 };
            string removedFolderPath = ToFolderPath(removedDirectoryPath);
            string staleOtherFolderPath = ToFolderPath(staleOtherDirectoryPath);
            var logs = new List<string>();

            File.Delete(removedPath);
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.RunInTransaction(() =>
                {
                    songDbConnection.CreateTable<LR2SongDB.song>();
                    songDbConnection.CreateTable<LR2SongDB.folder>();
                    songDbConnection.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(keepFile), typeof(LR2SongDB.song));
                    songDbConnection.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(removedFile), typeof(LR2SongDB.song));
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
                });
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

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", removedFolderPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", staleOtherFolderPath));
        });
    }

    /// <summary>
    /// nested BMS削除の範囲をroot直下へ広げず、譜面の残る祖先と別の兄弟folder行を保持する。
    /// </summary>
    [TestMethod]
    public void ApplyFileScanDiff_DeletedNestedBmsDoesNotExpandLr2NormalFolderScopeToRootChild()
    {

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
            ChartFile keepFile = ChartTestValues.Empty() with
            {
                Path = keepPath,
                Date = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(keepPath))
            };
            keepFile = keepFile with { Md5 = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(keepPath)).Md5 };
            ChartFile removedFile = ChartTestValues.Empty() with
            {
                Path = removedPath,
                Date = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(removedPath))
            };
            removedFile = removedFile with { Md5 = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(removedPath)).Md5 };
            string bigFolderPath = ToFolderPath(Path.Combine(lr2RootPath, "Big"));
            string removedFolderPath = ToFolderPath(removedDirectoryPath);
            string staleSiblingFolderPath = ToFolderPath(staleSiblingDirectoryPath);
            var logs = new List<string>();

            File.Delete(removedPath);
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.RunInTransaction(() =>
                {
                    songDbConnection.CreateTable<LR2SongDB.song>();
                    songDbConnection.CreateTable<LR2SongDB.folder>();
                    songDbConnection.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(keepFile), typeof(LR2SongDB.song));
                    songDbConnection.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(removedFile), typeof(LR2SongDB.song));
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
                });
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

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", removedFolderPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", bigFolderPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", staleSiblingFolderPath));
        });
    }

    /// <summary>
    /// 最後のnested BMSが消えると空になった子・祖先の通常folderを退役し、探索rootの行は維持する。
    /// </summary>
    [TestMethod]
    public void ApplyFileScanDiff_DeletedLastNestedBmsPrunesEmptyAncestorFolder()
    {

        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string packDirectoryPath = Path.Combine(lr2RootPath, "EmptyPack");
            string removedDirectoryPath = Path.Combine(packDirectoryPath, "Removed");
            Directory.CreateDirectory(removedDirectoryPath);
            string removedPath = Path.Combine(removedDirectoryPath, "removed.bms");
            File.WriteAllText(removedPath, CreateValidBmsText("Removed Last Nested"), Encoding.ASCII);
            ChartFile removedFile = ChartTestValues.Empty() with
            {
                Path = removedPath,
                Date = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(removedPath))
            };
            removedFile = removedFile with { Md5 = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(removedPath)).Md5 };
            string packFolderPath = ToFolderPath(packDirectoryPath);
            string removedFolderPath = ToFolderPath(removedDirectoryPath);

            File.Delete(removedPath);
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.RunInTransaction(() =>
                {
                    songDbConnection.CreateTable<LR2SongDB.song>();
                    songDbConnection.CreateTable<LR2SongDB.folder>();
                    songDbConnection.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(removedFile), typeof(LR2SongDB.song));
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
                });
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

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", removedFolderPath));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", packFolderPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", ToFolderPath(lr2RootPath)));
        });
    }

    /// <summary>
    /// root直下BMSの削除だけでは全rootのfolder同期を起こさず、無関係な古い兄弟folder行を保持する。
    /// </summary>
    [TestMethod]
    public void ApplyFileScanDiff_DeletedRootLevelBmsDoesNotExpandLr2NormalFolderScopeToRoot()
    {

        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string keepDirectoryPath = Path.Combine(lr2RootPath, "Keep");
            string staleDirectoryPath = Path.Combine(lr2RootPath, "StaleSibling");
            Directory.CreateDirectory(keepDirectoryPath);
            string keepPath = Path.Combine(keepDirectoryPath, "keep.bms");
            string removedPath = Path.Combine(lr2RootPath, "removed-root.bms");
            File.WriteAllText(keepPath, CreateValidBmsText("Keep Root Sibling"), Encoding.ASCII);
            File.WriteAllText(removedPath, CreateValidBmsText("Removed Root Level"), Encoding.ASCII);
            ChartFile keepFile = ChartTestValues.Empty() with
            {
                Path = keepPath,
                Date = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(keepPath))
            };
            keepFile = keepFile with { Md5 = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(keepPath)).Md5 };
            ChartFile removedFile = ChartTestValues.Empty() with
            {
                Path = removedPath,
                Date = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(removedPath))
            };
            removedFile = removedFile with { Md5 = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(removedPath)).Md5 };
            string staleFolderPath = ToFolderPath(staleDirectoryPath);
            var logs = new List<string>();

            File.Delete(removedPath);
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.RunInTransaction(() =>
                {
                    songDbConnection.CreateTable<LR2SongDB.song>();
                    songDbConnection.CreateTable<LR2SongDB.folder>();
                    songDbConnection.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(keepFile), typeof(LR2SongDB.song));
                    songDbConnection.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(removedFile), typeof(LR2SongDB.song));
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
                });
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

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", staleFolderPath));
        });
    }

    /// <summary>
    /// LR2以外のmodeでは新規BMSを処理しても通常folderを生成・削除せず、既存の古い行を保持する。
    /// </summary>
    [TestMethod]
    public void ApplyFileScanDiff_DoesNotSyncStaleLr2NormalFoldersOutsideLr2Mode()
    {

        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string packDirectoryPath = Path.Combine(lr2RootPath, "Pack");
            Directory.CreateDirectory(packDirectoryPath);
            string bmsPath = Path.Combine(packDirectoryPath, "added.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Folder Sync Disabled"), Encoding.ASCII);
            string stalePath = ToFolderPath(Path.Combine(lr2RootPath, "Stale"));

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.RunInTransaction(() =>
                {
                    songDbConnection.CreateTable<LR2SongDB.song>();
                    songDbConnection.CreateTable<LR2SongDB.folder>();
                    songDbConnection.InsertOrReplace(new LR2SongDB.folder
                    {
                        path = stalePath,
                        type = 1
                    }, typeof(LR2SongDB.folder));
                });
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
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", stalePath));
        });
    }

    /// <summary>
    /// 不完全scanでは通常folder同期とDB差分を作らず、元の失敗理由と既存folder行を保持する。
    /// </summary>
    [TestMethod]
    public void ApplyFileScanDiff_DoesNotSyncLr2NormalFoldersWhenScanIsIncomplete()
    {

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
                songDbConnection.RunInTransaction(() =>
                {
                    songDbConnection.CreateTable<LR2SongDB.song>();
                    songDbConnection.CreateTable<LR2SongDB.folder>();
                    songDbConnection.InsertOrReplace(new LR2SongDB.folder
                    {
                        path = stalePath,
                        type = 1
                    }, typeof(LR2SongDB.folder));
                });
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
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", stalePath));
        });
    }

    /// <summary>
    /// 譜面とdirectoryに差分がないscanでは通常folder同期を省き、古い別folderも既存行のまま保持する。
    /// </summary>
    [TestMethod]
    public void ApplyFileScanDiff_DoesNotSyncLr2NormalFoldersWhenThereIsNoDbDiff()
    {

        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string packDirectoryPath = Path.Combine(lr2RootPath, "Pack");
            Directory.CreateDirectory(packDirectoryPath);
            string bmsPath = Path.Combine(packDirectoryPath, "current.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Current"), Encoding.ASCII);
            var timestamp = new DateTime(2026, 6, 6, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, timestamp);
            Directory.SetLastWriteTimeUtc(packDirectoryPath, timestamp);
            Directory.SetLastWriteTimeUtc(lr2RootPath, timestamp);
            ChartFile currentFile = ChartTestValues.Empty() with
            {
                Path = bmsPath,
                Date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
            };
            currentFile = currentFile with { Txt = 0 };
            string stalePath = ToFolderPath(Path.Combine(lr2RootPath, "Stale"));
            var logs = new List<string>();

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.RunInTransaction(() =>
                {
                    songDbConnection.CreateTable<LR2SongDB.song>();
                    songDbConnection.CreateTable<LR2SongDB.folder>();
                    songDbConnection.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(currentFile), typeof(LR2SongDB.song));
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
                });
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
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(3L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", stalePath));
        });
    }

    /// <summary>
    /// BMSのtxt有無だけが変わる差分は通常folder同期へ広げず、無関係なfolder行を保持する。
    /// </summary>
    [TestMethod]
    public void ApplyFileScanDiff_DoesNotSyncLr2NormalFoldersForBmsTextOnlyDiff()
    {

        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "TextOnly");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "current.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Text Only"), Encoding.ASCII);
            var timestamp = new DateTime(2026, 6, 6, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, timestamp);
            Directory.SetLastWriteTimeUtc(chartDirectoryPath, timestamp);
            Directory.SetLastWriteTimeUtc(lr2RootPath, timestamp);
            ChartFile parsed = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsPath));
            ChartFile currentFile = ChartTestValues.Empty() with
            {
                Path = bmsPath,
                Date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
            };
            currentFile = currentFile with { Md5 = parsed.Md5 };
            currentFile = currentFile with { Txt = 0 };
            string stalePath = ToFolderPath(Path.Combine(lr2RootPath, "Stale"));
            var logs = new List<string>();

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.RunInTransaction(() =>
                {
                    songDbConnection.CreateTable<LR2SongDB.song>();
                    songDbConnection.CreateTable<LR2SongDB.folder>();
                    songDbConnection.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(currentFile), typeof(LR2SongDB.song));
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
                });
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
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", stalePath));
        });
    }

}
