using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
    public void LoadSongTable_DoesNotRegisterMaintenanceEncodingPropertyChangedHandler()
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
                    path = rootedChartPath,
                    folder = "folder",
                    parent = "parent"
                };
                song.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                songDb.InsertOrReplace(song, typeof(LR2SongDB.song));
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
            List<string> propertyNames = new List<string>();
            result.LoadedFiles[0].PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                propertyNames.Add(e.PropertyName);
            };

            result.LoadedFiles[0].maintenanceInfo.encoding = "utf-8";

            CollectionAssert.DoesNotContain(propertyNames, "encoding");
        });
    }

    [TestMethod]
    public void LoadSongTable_AppliesChartDigestMapToLoadedFiles()
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
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);

                TestableBmsFile song = new TestableBmsFile
                {
                    path = rootedChartPath,
                    folder = "folder",
                    parent = "parent"
                };
                song.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                songDb.InsertOrReplace(song, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(new LR2SongDBExtended.chart_digest_map
                {
                    md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    sha256 = new string('b', 64)
                }, typeof(LR2SongDBExtended.chart_digest_map));
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
            Assert.AreEqual(new string('b', 64), result.LoadedFiles[0].sha256);
            Assert.AreEqual(1, result.ChartDigestMap.Count);
        });
    }

    [TestMethod]
    public void LoadSongTable_PreservesShiftJisUnsupportedExistingSongAndWarns()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "Songs😀");
            Directory.CreateDirectory(chartDirectoryPath);
            string chartPath = Path.Combine(chartDirectoryPath, "chart.bms");
            File.WriteAllText(chartPath, CreateValidBmsText("Emoji Path"), Encoding.ASCII);

            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                TestableBmsFile song = new TestableBmsFile
                {
                    path = chartPath,
                    folder = "folder",
                    parent = "parent"
                };
                song.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                songDb.InsertOrReplace(song, typeof(LR2SongDB.song));
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
            Assert.AreEqual(0, result.DeletedSongPaths.Count);
            Assert.AreEqual(chartPath, result.LoadedFiles[0].path);
            Assert.IsTrue(string.IsNullOrWhiteSpace(result.LoadedFiles[0].folder));
            Assert.IsTrue(string.IsNullOrWhiteSpace(result.LoadedFiles[0].parent));
            Assert.IsTrue(result.LoadedFiles[0].Warnings.Contains(ChartWarningKind.Lr2PathEncodingUnsupported));

            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", chartPath));
            Assert.IsTrue(string.IsNullOrWhiteSpace(verify.ExecuteScalar<string>("SELECT parent FROM song WHERE path = ?;", chartPath)));
        });
    }

    [TestMethod]
    public void LoadSongTable_DoesNotPartiallyFixRelativeShiftJisUnsupportedPath()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string relativePath = Path.Combine("Songs😀", "chart.bms");
            string chartPath = Path.Combine(lr2RootPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(chartPath));
            File.WriteAllText(chartPath, CreateValidBmsText("Relative Emoji Path"), Encoding.ASCII);

            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                TestableBmsFile song = new TestableBmsFile
                {
                    path = relativePath,
                    folder = "folder",
                    parent = "parent"
                };
                song.SetHash("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
                songDb.InsertOrReplace(song, typeof(LR2SongDB.song));
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
            Assert.AreEqual(0, result.DeletedSongPaths.Count);
            Assert.AreEqual(relativePath, result.LoadedFiles[0].path);
            Assert.IsTrue(string.IsNullOrWhiteSpace(result.LoadedFiles[0].parent));
            Assert.IsTrue(result.LoadedFiles[0].Warnings.Contains(ChartWarningKind.Lr2PathEncodingUnsupported));

            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", relativePath));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", chartPath));
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
                    NativeBridgeUsed = true,
                    NativeBridgeMs = 234L,
                    NativeBridgeReason = "everything_bridge_fixed_scan",
                    ManagedDecodeMs = 12L,
                    ManagedMaterializeMs = 7L,
                    BridgeRawBufferBytes = 4096UL,
                    Result = CreateScanResult(
                        new[] { keepFile.path, Path.Combine(newDirectoryPath, "added.bms") },
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { keepDirectoryPath, Array.Empty<string>() },
                            { newDirectoryPath, Array.Empty<string>() }
                        })
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
            Assert.AreEqual(234L, result.NativeBridgeMs);
            Assert.AreEqual("everything_bridge_fixed_scan", result.NativeBridgeReason);
            Assert.AreEqual(12L, result.ManagedDecodeMs);
            Assert.AreEqual(7L, result.ManagedMaterializeMs);
            Assert.AreEqual(4096UL, result.BridgeRawBufferBytes);
            Assert.AreSame(keepFile, result.ClearedInstallDestinations.Single());
            Assert.IsNull(keepFile.instl_dst);
            CollectionAssert.Contains(result.NextFolderAllFileList.Keys.ToList(), keepDirectoryPath);
            CollectionAssert.Contains(result.NextFolderAllFileList.Keys.ToList(), newDirectoryPath);

            using LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.song>();
            List<BMSFile> dbFiles = songDb.Table<BMSFile>().ToList();
            Assert.AreEqual(1, dbFiles.Count);
            Assert.AreEqual(Path.Combine(newDirectoryPath, "added.bms"), dbFiles[0].path);
            List<LR2SongDBExtended.chart_digest_map> digestRows = songDb.Table<LR2SongDBExtended.chart_digest_map>().ToList();
            Assert.AreEqual(1, digestRows.Count);
            Assert.AreEqual(dbFiles[0].hash, digestRows[0].md5);
            Assert.AreEqual(result.AddedFiles[0].sha256, digestRows[0].sha256);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_ReportsCombinedBmsAndBmsonParseProgress()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "Added");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "added.bms");
            string bmsonPath = Path.Combine(chartDirectoryPath, "added.bmson");
            File.WriteAllText(bmsPath, "#PLAYER 1\r\n#TITLE Added\r\n");
            File.WriteAllText(bmsonPath, CreateBmsonJson("Added", "", "", "Artist", "Genre", 7, "beat-7k"));

            using (LR2SongDBExtended songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDbConnection);
            }

            int scanCompletedCount = 0;
            int fileDiffStartedCount = 0;
            object progressLock = new object();
            List<(int Total, int Processed, string Path)> progress = new List<(int, int, string)>();
            List<string> logs = new List<string>();
            BmsLibraryInitializationService service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                Array.Empty<BMSFile>(),
                new BmsScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        new[] { bmsPath, bmsonPath },
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                logInstallPerformance: (string message) => logs.Add(message),
                currentBmsonSongs: Array.Empty<LR2SongDBExtended.bmson_song>(),
                scanCompleted: () => scanCompletedCount++,
                fileDiffStarted: () => fileDiffStartedCount++,
                reportParseProgress: (total, processed, path) =>
                {
                    lock (progressLock)
                    {
                        progress.Add((total, processed, path));
                    }
                });

            Assert.AreEqual(1, scanCompletedCount);
            Assert.AreEqual(1, fileDiffStartedCount);
            Assert.AreEqual(1, result.AddedFiles.Count);
            Assert.AreEqual(1, result.AddedBmsonSongs.Count);
            Assert.AreEqual(1, result.BmsAddedTargetCount);
            Assert.AreEqual(1, result.BmsonUpsertTargetCount);
            Assert.AreEqual(1, result.FileDiffParserDegree);
            Assert.AreEqual(result.NewFileParseMs, result.BmsParseMs);
            Assert.IsTrue(result.BmsonParseMs >= 0);
            long expectedReadBytesEstimate = new FileInfo(bmsPath).Length + new FileInfo(bmsonPath).Length;
            Assert.AreEqual(expectedReadBytesEstimate, result.ParseReadBytesEstimate);
            Assert.IsTrue(progress.Any(item => item.Total == 2 && item.Processed == 0));
            Assert.IsTrue(progress.Any(item => item.Total == 2 && item.Processed == 2));
            Assert.IsTrue(progress.All(item => item.Total == 2));
            Assert.IsTrue(logs.Any((string message) => message.Contains("bms_added_target_count=1")
                && message.Contains("bmson_upsert_target_count=1")
                && message.Contains("file_diff_parser_degree=1")
                && message.Contains("inline_chart_info_target_count=2")
                && message.Contains("parse_read_bytes_estimate=" + expectedReadBytesEstimate)));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_AddsBmsWithLr2FolderAndParentHashes()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "Lr2Crc");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "added.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("CRC Added"), Encoding.ASCII);

            using (LR2SongDBExtended songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            BmsLibraryInitializationService service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                Array.Empty<BMSFile>(),
                new BmsScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        new[] { bmsPath },
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: Array.Empty<LR2SongDBExtended.bmson_song>());

            Assert.AreEqual(1, result.AddedFiles.Count);
            BMSFile added = result.AddedFiles[0];
            Assert.IsFalse(string.IsNullOrWhiteSpace(added.folder));
            Assert.IsFalse(string.IsNullOrWhiteSpace(added.parent));
            Assert.IsTrue(added.parent.Length <= 8);
            Assert.IsFalse(added.Warnings.Contains(ChartWarningKind.Lr2PathEncodingUnsupported));
            Assert.AreEqual(0, result.NextFiles.Count((BMSFile file) => string.IsNullOrWhiteSpace(file.parent)));

            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(added.folder, verify.ExecuteScalar<string>("SELECT folder FROM song WHERE path = ?;", bmsPath));
            Assert.AreEqual(added.parent, verify.ExecuteScalar<string>("SELECT parent FROM song WHERE path = ?;", bmsPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_PreservesShiftJisUnsupportedPathWithoutParentAndWarns()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "Emoji😀");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "added.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Emoji Added"), Encoding.ASCII);

            using (LR2SongDBExtended songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            BmsLibraryInitializationService service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                Array.Empty<BMSFile>(),
                new BmsScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        new[] { bmsPath },
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: Array.Empty<LR2SongDBExtended.bmson_song>());

            Assert.AreEqual(1, result.AddedFiles.Count);
            BMSFile added = result.AddedFiles[0];
            Assert.IsTrue(string.IsNullOrWhiteSpace(added.folder));
            Assert.IsTrue(string.IsNullOrWhiteSpace(added.parent));
            Assert.IsTrue(added.Warnings.Contains(ChartWarningKind.Lr2PathEncodingUnsupported));
            StringAssert.Contains(added.WarningTooltipText, "Shift_JIS");
            Assert.AreEqual(1, result.NextFiles.Count((BMSFile file) => string.IsNullOrWhiteSpace(file.parent)));

            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", bmsPath));
            Assert.IsTrue(string.IsNullOrWhiteSpace(verify.ExecuteScalar<string>("SELECT parent FROM song WHERE path = ?;", bmsPath)));
        });
    }

    [TestMethod]
    public void UpsertSongs_FillsLr2FolderAndParentForInstalledBmsRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "Installed");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "chart.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Installed"), Encoding.ASCII);
            using (LR2SongDBExtended songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            TestableBmsFile file = new TestableBmsFile
            {
                path = bmsPath,
                folder = null,
                parent = null
            };
            file.SetHash("cccccccccccccccccccccccccccccccc");

            new BmsLibraryDbGateway(songDbPath).UpsertSongs(new[] { file });

            Assert.IsFalse(string.IsNullOrWhiteSpace(file.folder));
            Assert.IsFalse(string.IsNullOrWhiteSpace(file.parent));
            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(file.folder, verify.ExecuteScalar<string>("SELECT folder FROM song WHERE path = ?;", bmsPath));
            Assert.AreEqual(file.parent, verify.ExecuteScalar<string>("SELECT parent FROM song WHERE path = ?;", bmsPath));
        });
    }

    [TestMethod]
    public void UpsertSongs_PreservesShiftJisUnsupportedInstalledBmsRowsWithWarning()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "Installed😀");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "chart.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Installed Emoji"), Encoding.ASCII);
            using (LR2SongDBExtended songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            TestableBmsFile file = new TestableBmsFile
            {
                path = bmsPath,
                folder = null,
                parent = null
            };
            file.SetHash("dddddddddddddddddddddddddddddddd");

            new BmsLibraryDbGateway(songDbPath).UpsertSongs(new[] { file });

            Assert.IsTrue(string.IsNullOrWhiteSpace(file.parent));
            Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.Lr2PathEncodingUnsupported));
            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", bmsPath));
            Assert.IsTrue(string.IsNullOrWhiteSpace(verify.ExecuteScalar<string>("SELECT parent FROM song WHERE path = ?;", bmsPath)));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_AddsBmsAndPersistsInlineChartInfo()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "InlineInfo");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "added.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Inline Added"), Encoding.ASCII);

            using (LR2SongDBExtended songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            BmsLibraryInitializationService service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                Array.Empty<BMSFile>(),
                new BmsScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        new[] { bmsPath },
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: Array.Empty<LR2SongDBExtended.bmson_song>());

            Assert.AreEqual(1, result.AddedFiles.Count);
            Assert.AreEqual(1, result.InlineChartInfoTargetCount);
            Assert.AreEqual(1, result.InlineChartInfoSuccessCount);
            Assert.AreEqual(0, result.InlineChartInfoParseFailedCount);
            Assert.AreEqual(1, result.InlineChartInfoRows.Count);
            Assert.AreEqual(1, result.InlineChartInfoAppliedRows.Count);
            Assert.IsNotNull(result.AddedFiles[0].ChartInfo);

            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ? AND md5 = ?;", result.AddedFiles[0].sha256, result.AddedFiles[0].hash));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_parse_failure;"));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_ChartInfoParseFailureDoesNotBlockSongRegistration()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "InlineFailure");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "bad.bms");
            File.WriteAllText(bmsPath, "#PLAYER 1\r\n#TITLE Bad\r\n#00111:01\r\n", Encoding.ASCII);

            using (LR2SongDBExtended songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            BmsLibraryInitializationService service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                Array.Empty<BMSFile>(),
                new BmsScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        new[] { bmsPath },
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: Array.Empty<LR2SongDBExtended.bmson_song>());

            Assert.AreEqual(1, result.AddedFiles.Count);
            Assert.AreEqual(1, result.InlineChartInfoTargetCount);
            Assert.AreEqual(1, result.InlineChartInfoParseFailedCount);
            Assert.AreEqual(1, result.InlineChartInfoFailurePersistedCount);
            Assert.AreEqual(0, result.InlineChartInfoRows.Count);

            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song;"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
            LR2SongDBExtended.chart_info_parse_failure failure = verify.Query<LR2SongDBExtended.chart_info_parse_failure>("SELECT * FROM chart_info_parse_failure WHERE md5 = ?;", result.AddedFiles[0].hash).Single();
            Assert.AreEqual("parse_failed", failure.failure_kind);
            Assert.AreEqual(BmsLibraryDbGateway.CurrentChartInfoParserVersion, failure.parser_version);
            Assert.IsTrue(failure.message.Length > 0);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_CurrentParseFailureSkipsInlineChartInfoParse()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "InlineFailureSkip");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "skip.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Inline Failure Skip"), Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(bmsPath);

            using (LR2SongDBExtended songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDbConnection);
                songDbConnection.InsertOrReplace(
                    CreateChartInfoParseFailureRow(snapshot.Md5, snapshot.Sha256, bmsPath),
                    typeof(LR2SongDBExtended.chart_info_parse_failure));
            }

            BmsLibraryInitializationService service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                Array.Empty<BMSFile>(),
                new BmsScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        new[] { bmsPath },
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: Array.Empty<LR2SongDBExtended.bmson_song>());

            Assert.AreEqual(1, result.AddedFiles.Count);
            Assert.AreEqual(1, result.InlineChartInfoTargetCount);
            Assert.AreEqual(1, result.InlineChartInfoFailureSkippedCount);
            Assert.AreEqual(0, result.InlineChartInfoSuccessCount);
            Assert.AreEqual(0, result.InlineChartInfoRows.Count);
            Assert.IsNull(result.AddedFiles[0].ChartInfo);

            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song;"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_parse_failure WHERE md5 = ?;", snapshot.Md5));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_CurrentInlineChartInfoRowSkipsParseAndAppliesToModel()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "InlineCurrent");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "current.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Inline Current"), Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(bmsPath);

            using (LR2SongDBExtended songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }
            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertChartInfos(new[] { CreateMinimalChartInfoRow(snapshot.Sha256, snapshot.Md5) });

            BmsLibraryInitializationService service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                gateway,
                new BmsLibraryOptionsSnapshot(),
                Array.Empty<BMSFile>(),
                new BmsScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        new[] { bmsPath },
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: Array.Empty<LR2SongDBExtended.bmson_song>());

            Assert.AreEqual(1, result.AddedFiles.Count);
            Assert.AreEqual(1, result.InlineChartInfoTargetCount);
            Assert.AreEqual(1, result.InlineChartInfoCurrentSkippedCount);
            Assert.AreEqual(0, result.InlineChartInfoSuccessCount);
            Assert.AreEqual(0, result.InlineChartInfoRows.Count);
            Assert.AreEqual(1, result.InlineChartInfoAppliedRows.Count);
            Assert.IsNotNull(result.AddedFiles[0].ChartInfo);
            Assert.AreEqual(7, result.AddedFiles[0].ChartInfo.level);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_FileDiffParserDegree_UsesDefaultAndNormalizesOverrides()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            int expectedDefault = Math.Min(4, Math.Max(1, Environment.ProcessorCount - 1));
            Assert.AreEqual(expectedDefault, BmsLibraryInitializationService.ResolveDefaultFileDiffParserDegree());

            Assert.AreEqual(expectedDefault, RunWithParserDegreeOverride(null, songDbPath).FileDiffParserDegree);
            Assert.AreEqual(1, RunWithParserDegreeOverride(0, songDbPath).FileDiffParserDegree);
            Assert.AreEqual(1, RunWithParserDegreeOverride(-10, songDbPath).FileDiffParserDegree);
            Assert.AreEqual(2, RunWithParserDegreeOverride(2, songDbPath).FileDiffParserDegree);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_InvalidBmsonLogsAndReportsProgress()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "InvalidBmson");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsonPath = Path.Combine(chartDirectoryPath, "invalid.bmson");
            File.WriteAllText(bmsonPath, "{\"version\":\"1.0.0\",\"info\":");

            using (LR2SongDBExtended songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDbConnection);
            }

            object progressLock = new object();
            List<(int Total, int Processed, string Path)> progress = new List<(int, int, string)>();
            List<string> logs = new List<string>();
            BmsLibraryInitializationService service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                Array.Empty<BMSFile>(),
                new BmsScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        new[] { bmsonPath },
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                logEverythingScan: (string message) => logs.Add(message),
                currentBmsonSongs: Array.Empty<LR2SongDBExtended.bmson_song>(),
                reportParseProgress: (total, processed, path) =>
                {
                    lock (progressLock)
                    {
                        progress.Add((total, processed, path));
                    }
                });

            Assert.AreEqual(0, result.AddedFiles.Count);
            Assert.AreEqual(0, result.AddedBmsonSongs.Count);
            Assert.AreEqual(0, result.BmsAddedTargetCount);
            Assert.AreEqual(1, result.BmsonUpsertTargetCount);
            Assert.AreEqual(new FileInfo(bmsonPath).Length, result.ParseReadBytesEstimate);
            Assert.IsTrue(progress.Any(item => item.Total == 1 && item.Processed == 0));
            Assert.IsTrue(progress.Any(item => item.Total == 1 && item.Processed == 1 && string.Equals(item.Path, bmsonPath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(logs.Any((string message) => message.Contains("bmson_parse_failed") && message.Contains("invalid.bmson")));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_BuildsCachesDirectlyFromScanHashes()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "Keep");
            string chartPath = Path.Combine(chartDirectoryPath, "keep.bms");
            Directory.CreateDirectory(chartDirectoryPath);
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Keep\r\n");

            TestableBmsFile keepFile = new TestableBmsFile
            {
                path = chartPath
            };
            keepFile.SetHash(BMSFile.CreateBMSFileFromFile(chartPath).hash);

            uint audioBaseHash = BMSDirectoryFileNameHash.GetFileNameHash("sound.wav");
            uint imageBaseHash = BMSDirectoryFileNameHash.GetFileNameHash("bg.png");
            uint audioRelativeHash = BMSDirectoryFileNameHash.GetLookupHash("sound\\sound.wav");
            uint[] allBaseHashes = new[] { audioBaseHash, imageBaseHash };

            BmsLibraryInitializationService service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                new[] { keepFile },
                new BmsScanExecutionResult
                {
                    Success = true,
                    Result = new BmsScanResult
                    {
                        ChartFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { chartPath },
                        ChartDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { chartDirectoryPath },
                        AllResourceBaseNameHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, allBaseHashes }
                        },
                        AudioBaseNameHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, new[] { audioBaseHash } }
                        },
                        ImageBaseNameHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, new[] { imageBaseHash } }
                        },
                        MovieBaseNameHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<uint>() }
                        },
                        AudioRelativePathHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, new[] { audioRelativeHash } }
                        },
                        ImageRelativePathHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<uint>() }
                        },
                        MovieRelativePathHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<uint>() }
                        }
                    }
                },
                0L,
                () => null,
                null);

            CollectionAssert.AreEquivalent(allBaseHashes, result.NextFolderAllFileList.TryGetCachedFileNameHashArray(chartDirectoryPath));
            DirectoryResourceLookupCache.Entry entry = result.NextDirectoryResourceLookupCache.GetEntryOrNull(chartDirectoryPath);
            Assert.IsNotNull(entry);
            Assert.AreEqual(2, entry.FileNameHashCount);
            Assert.AreEqual(1, entry.AudioFileNameHashCount);
            Assert.AreEqual(1, entry.ImageFileNameHashCount);
            Assert.AreEqual(0, entry.MovieFileNameHashCount);
            Assert.IsTrue(entry.AudioBaseNameHashes.Contains(audioBaseHash));
            Assert.IsTrue(entry.ImageBaseNameHashes.Contains(imageBaseHash));
            Assert.IsTrue(entry.AudioRelativePathHashes.Contains(audioRelativeHash));
            DirectoryRelativePathHashIndex.Entry? relativePathEntry = result.NextDirectoryRelativePathHashIndex?.GetEntryOrNull(chartDirectoryPath);
            Assert.IsNotNull(relativePathEntry);
            DirectoryRelativePathHashIndex.Entry actualRelativePathEntry = relativePathEntry!;
            Assert.IsTrue(actualRelativePathEntry.AudioBaseNameHashes.Contains(audioBaseHash));
            Assert.IsTrue(actualRelativePathEntry.AudioRelativePathHashes.Contains(audioRelativeHash));
            Assert.AreEqual((ulong)2, result.AllBaseHashEntryCount);
            Assert.AreEqual((ulong)1, result.AudioBaseHashEntryCount);
            Assert.AreEqual((ulong)1, result.ImageBaseHashEntryCount);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_RemovesOrphanChartDigestRowsForDeletedSongs()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate(string lr2RootPath, string songDbPath)
        {
            string deletedChartPath = Path.Combine(lr2RootPath, "Deleted", "deleted.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(deletedChartPath));
            File.WriteAllText(deletedChartPath, "#PLAYER 1\r\n#TITLE Deleted\r\n");

            TestableBmsFile deletedFile = new TestableBmsFile
            {
                path = deletedChartPath
            };
            BMSFile source = BMSFile.CreateBMSFileFromFile(deletedChartPath);
            deletedFile.SetHash(source.hash);
            deletedFile.SetSha256(source.sha256);

            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(deletedFile, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(new LR2SongDBExtended.chart_digest_map
                {
                    md5 = deletedFile.hash,
                    sha256 = deletedFile.sha256
                }, typeof(LR2SongDBExtended.chart_digest_map));
            }

            BmsLibraryInitializationService service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                new[] { deletedFile },
                new BmsScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(Array.Empty<string>(), new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                },
                0L,
                () => null,
                null);

            CollectionAssert.Contains(result.DeletedPaths, deletedChartPath);
            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + deletedFile.hash + "';"));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_KeepsSharedChartDigestRowsWhenAnotherSongStillUsesSameMd5()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate(string lr2RootPath, string songDbPath)
        {
            string keepChartPath = Path.Combine(lr2RootPath, "Keep", "keep.bms");
            string deletedChartPath = Path.Combine(lr2RootPath, "Deleted", "deleted.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(keepChartPath));
            Directory.CreateDirectory(Path.GetDirectoryName(deletedChartPath));
            File.WriteAllText(keepChartPath, "#PLAYER 1\r\n#TITLE Same\r\n");
            File.Copy(keepChartPath, deletedChartPath, overwrite: true);

            BMSFile sourceKeep = BMSFile.CreateBMSFileFromFile(keepChartPath);
            BMSFile sourceDeleted = BMSFile.CreateBMSFileFromFile(deletedChartPath);
            TestableBmsFile keepFile = new TestableBmsFile
            {
                path = keepChartPath
            };
            keepFile.SetHash(sourceKeep.hash);
            keepFile.SetSha256(sourceKeep.sha256);
            TestableBmsFile deletedFile = new TestableBmsFile
            {
                path = deletedChartPath
            };
            deletedFile.SetHash(sourceDeleted.hash);
            deletedFile.SetSha256(sourceDeleted.sha256);

            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(keepFile, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(deletedFile, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(new LR2SongDBExtended.chart_digest_map
                {
                    md5 = keepFile.hash,
                    sha256 = keepFile.sha256
                }, typeof(LR2SongDBExtended.chart_digest_map));
            }

            BmsLibraryInitializationService service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                new[] { keepFile, deletedFile },
                new BmsScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        new[] { keepChartPath },
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(keepChartPath), Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null);

            CollectionAssert.Contains(result.DeletedPaths, deletedChartPath);
            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + keepFile.hash + "';"));
        });
    }

    [TestMethod]
    public void BackfillChartDigests_ComputesOnlyMissingHashesAndPersistsThem()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartAPath = Path.Combine(lr2RootPath, "Songs", "a.bms");
            string chartBPath = Path.Combine(lr2RootPath, "Songs", "b.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(chartAPath));
            File.WriteAllText(chartAPath, "#PLAYER 1\r\n#TITLE A\r\n");
            File.WriteAllText(chartBPath, "#PLAYER 1\r\n#TITLE B\r\n");

            TestableBmsFile chartA = new TestableBmsFile
            {
                path = chartAPath
            };
            chartA.SetHash(BMSFile.CreateBMSFileFromFile(chartAPath).hash);
            TestableBmsFile chartB = new TestableBmsFile
            {
                path = chartBPath
            };
            chartB.SetHash(BMSFile.CreateBMSFileFromFile(chartBPath).hash);
            chartB.SetSha256(new string('c', 64));

            BmsLibraryInitializationService service = new BmsLibraryInitializationService();
            List<(int Total, int Processed, string Path)> progress = new List<(int, int, string)>();
            ChartDigestBackfillResult result = service.BackfillChartDigests(
                new BmsLibraryDbGateway(songDbPath),
                new[] { chartA, chartB },
                (total, processed, path) => progress.Add((total, processed, path)));

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(0, result.FailedCount);
            Assert.IsFalse(string.IsNullOrWhiteSpace(chartA.sha256));
            Assert.AreEqual(new string('c', 64), chartB.sha256);
            Assert.IsTrue(progress.Any((item) => item.Total == 1 && item.Processed == 1));

            using LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath);
            List<LR2SongDBExtended.chart_digest_map> rows = songDb.Table<LR2SongDBExtended.chart_digest_map>().ToList();
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(chartA.hash, rows[0].md5);
            Assert.AreEqual(chartA.sha256, rows[0].sha256);
        });
    }

    [TestMethod]
    public void LoadSongTable_LoadsBmsonSongsFromCatalogTable()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsonPath = Path.Combine(lr2RootPath, "Songs", "chart.bmson");
            Directory.CreateDirectory(Path.GetDirectoryName(bmsonPath));
            File.WriteAllText(bmsonPath, CreateBmsonJson("Title", "Sub", "Chart", "Artist", "Genre", 12, "beat-7k"));

            LR2SongDBExtended.bmson_song row = BmsonSongParser.Parse(bmsonPath);
            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(row, typeof(LR2SongDBExtended.bmson_song));
            }

            BmsLibraryInitializationService service = new BmsLibraryInitializationService();
            SongTableLoadResult result = service.LoadSongTable(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                null,
                new TestFileMutationService(),
                null,
                ex => ex.Message);

            Assert.AreEqual(1, result.LoadedBmsonSongs.Count);
            Assert.AreEqual(bmsonPath, result.LoadedBmsonSongs[0].path);
            Assert.AreEqual("Title", result.LoadedBmsonSongs[0].title);
            Assert.AreEqual("Sub [Chart]", result.LoadedBmsonSongs[0].subtitle);
        });
    }

    [TestMethod]
    public void LoadSongTable_DoesNotHydrateChartInfoOnCriticalPath()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartPath = Path.Combine(lr2RootPath, "Songs", "chart.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(chartPath));
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n");
            string md5 = BMSFile.CreateBMSFileFromFile(chartPath).hash;
            string sha256 = new string('1', 64);

            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                TestableBmsFile song = new TestableBmsFile
                {
                    path = chartPath
                };
                song.SetHash(md5);
                songDb.InsertOrReplace(song, typeof(LR2SongDB.song));
                songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
                songDb.InsertOrReplace(new LR2SongDBExtended.chart_digest_map
                {
                    md5 = md5,
                    sha256 = sha256
                }, typeof(LR2SongDBExtended.chart_digest_map));
            }
            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertChartInfos(new[] { CreateMinimalChartInfoRow(sha256, md5) });

            BmsLibraryInitializationService service = new BmsLibraryInitializationService();
            SongTableLoadResult result = service.LoadSongTable(
                gateway,
                new BmsLibraryOptionsSnapshot(),
                null,
                new TestFileMutationService(),
                null,
                ex => ex.Message);

            Assert.AreEqual(1, result.LoadedFiles.Count);
            Assert.AreEqual(sha256, result.LoadedFiles[0].sha256);
            Assert.IsNull(result.LoadedFiles[0].ChartInfo);
            Assert.AreEqual(0L, result.ChartInfoMapLoadMs);
            Assert.AreEqual(0L, result.ChartInfoApplyMs);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_TracksBmsonAddsDeletesAndUpdatesDatabase()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string keepBmsonPath = Path.Combine(lr2RootPath, "Keep", "keep.bmson");
            string addedBmsonPath = Path.Combine(lr2RootPath, "Added", "added.bmson");
            string deletedBmsonPath = Path.Combine(lr2RootPath, "Deleted", "deleted.bmson");
            Directory.CreateDirectory(Path.GetDirectoryName(keepBmsonPath));
            Directory.CreateDirectory(Path.GetDirectoryName(addedBmsonPath));
            File.WriteAllText(keepBmsonPath, CreateBmsonJson("Keep", "", "", "Artist", "Genre", 5, "beat-5k"));
            File.WriteAllText(addedBmsonPath, CreateBmsonJson("Added", "", "", "Artist", "Genre", 7, "beat-7k"));

            LR2SongDBExtended.bmson_song keepSong = BmsonSongParser.Parse(keepBmsonPath);
            LR2SongDBExtended.bmson_song deletedSong = new LR2SongDBExtended.bmson_song
            {
                path = deletedBmsonPath,
                folder = Path.GetDirectoryName(deletedBmsonPath),
                title = "Deleted",
                md5 = new string('a', 32),
                sha256 = new string('b', 64),
                updated_at = DateTime.UtcNow.AddDays(-1)
            };
            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(keepSong, typeof(LR2SongDBExtended.bmson_song));
                songDb.InsertOrReplace(deletedSong, typeof(LR2SongDBExtended.bmson_song));
            }

            BmsLibraryInitializationService service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                Array.Empty<BMSFile>(),
                new BmsScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(Array.Empty<string>(), new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: new[] { keepSong, deletedSong },
                executeBmsonScan: () => new BmsScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        new[] { keepBmsonPath, addedBmsonPath },
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(keepBmsonPath), Array.Empty<string>() },
                            { Path.GetDirectoryName(addedBmsonPath), Array.Empty<string>() }
                        })
                });

            CollectionAssert.Contains(result.DeletedBmsonPaths, deletedBmsonPath);
            Assert.AreEqual(1, result.AddedBmsonSongs.Count);
            Assert.AreEqual(2, result.NextBmsonSongs.Count);
            Assert.IsTrue(result.NextBmsonSongs.Any((LR2SongDBExtended.bmson_song song) => string.Equals(song.path, keepBmsonPath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(result.NextBmsonSongs.Any((LR2SongDBExtended.bmson_song song) => string.Equals(song.path, addedBmsonPath, StringComparison.OrdinalIgnoreCase)));

            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            List<LR2SongDBExtended.bmson_song> rows = verify.Table<LR2SongDBExtended.bmson_song>().ToList();
            Assert.AreEqual(2, rows.Count);
            Assert.IsTrue(rows.Any((LR2SongDBExtended.bmson_song song) => string.Equals(song.path, keepBmsonPath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(rows.Any((LR2SongDBExtended.bmson_song song) => string.Equals(song.path, addedBmsonPath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(rows.Any((LR2SongDBExtended.bmson_song song) => string.Equals(song.path, deletedBmsonPath, StringComparison.OrdinalIgnoreCase)));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_UpdatedBmsonUsesSnapshotTimestamp()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsonPath = Path.Combine(lr2RootPath, "Updated", "chart.bmson");
            Directory.CreateDirectory(Path.GetDirectoryName(bmsonPath));
            File.WriteAllText(bmsonPath, CreateBmsonJson("Old", "", "", "Artist", "Genre", 5, "beat-5k"));
            DateTime oldTimestamp = new DateTime(2026, 5, 1, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsonPath, oldTimestamp);
            LR2SongDBExtended.bmson_song existingSong = BmsonSongParser.Parse(bmsonPath);

            File.WriteAllText(bmsonPath, CreateBmsonJson("New", "", "", "Artist", "Genre", 7, "beat-7k"));
            DateTime newTimestamp = new DateTime(2026, 5, 2, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsonPath, newTimestamp);

            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(existingSong, typeof(LR2SongDBExtended.bmson_song));
            }

            BmsLibraryInitializationService service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                Array.Empty<BMSFile>(),
                new BmsScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        new[] { bmsonPath },
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(bmsonPath), Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: new[] { existingSong });

            Assert.AreEqual(1, result.AddedBmsonSongs.Count);
            Assert.AreEqual("New", result.AddedBmsonSongs[0].title);
            Assert.AreEqual(newTimestamp, result.AddedBmsonSongs[0].updated_at);
            Assert.AreEqual(1, result.NextBmsonSongs.Count);
            Assert.AreEqual("New", result.NextBmsonSongs[0].title);

            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            LR2SongDBExtended.bmson_song row = verify.Table<LR2SongDBExtended.bmson_song>().Single();
            Assert.AreEqual("New", row.title);
            Assert.AreEqual(newTimestamp, row.updated_at);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_MergesBmsonOnlyDirectoriesIntoInstallCandidateCache()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsDir = Path.Combine(lr2RootPath, "BmsKeep");
            string bmsonDir = Path.Combine(lr2RootPath, "BmsonOnly");
            Directory.CreateDirectory(bmsDir);
            Directory.CreateDirectory(bmsonDir);
            string bmsPath = Path.Combine(bmsDir, "keep.bms");
            string bmsonPath = Path.Combine(bmsonDir, "chart.bmson");
            File.WriteAllText(bmsPath, "#PLAYER 1\r\n#TITLE Keep\r\n");
            File.WriteAllText(bmsonPath, CreateBmsonJson("Title", "Sub", "Chart", "Artist", "Genre", 12, "beat-7k"));

            TestableBmsFile keepFile = new TestableBmsFile
            {
                path = bmsPath
            };
            keepFile.SetHash(BMSFile.CreateBMSFileFromFile(bmsPath).hash);

            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(keepFile, typeof(LR2SongDB.song));
            }

            BmsLibraryInitializationService service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                new[] { keepFile },
                new BmsScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        new[] { bmsPath },
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { bmsDir, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                dialogService: null,
                logInstallPerformance: null,
                logEverythingScan: null,
                currentBmsonSongs: Array.Empty<LR2SongDBExtended.bmson_song>(),
                executeBmsonScan: () => new BmsScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        new[] { bmsonPath },
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { bmsonDir, new[] { Path.Combine("sound", "song.ogg") } }
                        })
                });

            CollectionAssert.Contains(result.NextFolderAllFileList.Keys.ToList(), bmsDir);
            CollectionAssert.Contains(result.NextFolderAllFileList.Keys.ToList(), bmsonDir);
            Assert.IsNotNull(result.NextDirectoryResourceLookupCache);
            Assert.IsTrue(result.NextDirectoryResourceLookupCache.Keys.Contains(bmsonDir, StringComparer.OrdinalIgnoreCase));
            DirectoryResourceLookupCache.Entry bmsonEntry = result.NextDirectoryResourceLookupCache.GetEntryOrNull(bmsonDir);
            Assert.IsNotNull(bmsonEntry);
            Assert.AreEqual(1, bmsonEntry.AudioFileNameHashCount);
            Assert.AreEqual(1, bmsonEntry.AudioRelativePathHashArray.Length);
            Assert.AreEqual(0, result.NextFolderAllFileList.TryGetCachedFileNameHashArray(bmsDir)?.Length ?? -1);
            Assert.AreEqual(1, result.NextFolderAllFileList.TryGetCachedFileNameHashArray(bmsonDir)?.Length ?? -1);
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
                file => string.Equals(file?.hash, installedHash, StringComparison.OrdinalIgnoreCase),
                file =>
                {
                    file.SetWarning(ChartWarningKind.ResourceWavMissing, "strict");
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
            Assert.IsTrue(installedWarningPackage.BMSFiles[0].Warnings.Contains(ChartWarningKind.AlreadyInstalled));
            Assert.IsTrue(singleFileWarningPackage.BMSFiles[0].Warnings.Contains(ChartWarningKind.SingleBmsFile));
        });
    }

    [TestMethod]
    public void LoadInstallTable_RestoresNestedChartsAndPrioritizesNestedWarning()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string directoryPackagePath = Path.Combine(lr2RootPath, "PendingDir");
            string nestedDirectoryPath = Path.Combine(directoryPackagePath, "sub");
            Directory.CreateDirectory(nestedDirectoryPath);
            File.WriteAllText(Path.Combine(directoryPackagePath, "root.bms"), "#PLAYER 1\r\n#TITLE Root\r\n");
            File.WriteAllText(Path.Combine(nestedDirectoryPath, "another.bms"), "#PLAYER 1\r\n#TITLE Nested\r\n#WAVAA missing.wav\r\n#00111:AA\r\n");

            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(new BMSPackage
                {
                    path = directoryPackagePath,
                    delete_parent = false
                }, typeof(LR2SongDBExtended.install));
            }

            BmsLibraryMaintenanceService maintenanceService = new BmsLibraryMaintenanceService();
            BmsLibraryInitializationService service = new BmsLibraryInitializationService();
            InstallTableLoadResult result = service.LoadInstallTable(
                new BmsLibraryDbGateway(songDbPath),
                file => false,
                file => maintenanceService.ApplyNeedToBeFixedWarnings(file, file.maintenanceInfo, strictCheck: true));

            Assert.AreEqual(1, result.PendingPackages.Count);
            Assert.AreEqual(2, result.PendingPackages[0].BMSFiles.Count);
            BMSFile nestedChart = result.PendingPackages[0].BMSFiles.Single((BMSFile file) => Path.GetFileName(file.path).Equals("another.bms", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(nestedChart.Warnings.Contains(ChartWarningKind.NestedChartFileInPackage));
            Assert.IsTrue(nestedChart.Warnings.Contains(ChartWarningKind.ResourceWavMissing));
            Assert.AreEqual("[2] " + BeMusicSeeker.Properties.Resources.WarningDigest_NestedChart + ", " + BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing, nestedChart.WarningDigestText);
            StringAssert.Contains(nestedChart.WarningTooltipText, BeMusicSeeker.Properties.Resources.Warning_NestedChartFileInPackage);
            StringAssert.Contains(nestedChart.WarningTooltipText, "WAV");
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

    [TestMethod]
    public void LoadInstallTable_AssignsBmsonSingleFileWarning()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string singleFileDirectoryPath = Path.Combine(lr2RootPath, "Pending");
            Directory.CreateDirectory(singleFileDirectoryPath);
            string singleFileChartPath = Path.Combine(singleFileDirectoryPath, "single_chart.bmson");
            File.WriteAllText(singleFileChartPath, "{\"version\":\"1.0.0\",\"info\":{\"title\":\"Single\",\"artist\":\"Artist\"},\"lines\":[{\"y\":0}]}");

            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(new BMSPackage
                {
                    path = singleFileChartPath,
                    delete_parent = false
                }, typeof(LR2SongDBExtended.install));
            }

            BmsLibraryInitializationService service = new BmsLibraryInitializationService();
            InstallTableLoadResult result = service.LoadInstallTable(
                new BmsLibraryDbGateway(songDbPath),
                file => false,
                file =>
                {
                    file.SetWarning(ChartWarningKind.ResourceWavMissing, "strict");
                    return true;
                });

            Assert.AreEqual(1, result.PendingPackages.Count);
            Assert.AreEqual(1, result.PendingWarningInitTargets.Count);
            Assert.AreEqual(1, result.SingleFileWarningCount);
            Assert.IsTrue(result.PendingPackages[0].BMSFiles[0].Warnings.Contains(ChartWarningKind.SingleBmsonFile));
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

    private static string CreateBmsonJson(string title, string subtitle, string chartName, string artist, string genre, int level, string modeHint)
    {
        return "{"
            + "\"version\":\"1.0.0\","
            + "\"info\":{"
            + "\"title\":\"" + title + "\","
            + "\"subtitle\":\"" + subtitle + "\","
            + "\"chart_name\":\"" + chartName + "\","
            + "\"artist\":\"" + artist + "\","
            + "\"genre\":\"" + genre + "\","
            + "\"level\":" + level + ","
            + "\"mode_hint\":\"" + modeHint + "\","
            + "\"banner_image\":\"banner.png\","
            + "\"back_image\":\"back.png\","
            + "\"eyecatch_image\":\"stage.png\","
            + "\"preview_music\":\"preview.ogg\","
            + "\"subartists\":[\"SubA\",\"SubB\"]"
            + "},"
            + "\"sound_channels\":[],"
            + "\"bpm_events\":[],"
            + "\"lines\":[{\"y\":0}]"
            + "}";
    }

    private static string CreateValidBmsText(string title)
    {
        return "#PLAYER 1\r\n"
            + "#TITLE " + title + "\r\n"
            + "#ARTIST Artist\r\n"
            + "#BPM 120\r\n"
            + "#WAV01 sound.wav\r\n"
            + "#00111:01\r\n";
    }

    private static SongTableFileCheckResult RunWithParserDegreeOverride(int? overrideValue, string songDbPath)
    {
        BmsLibraryInitializationService service = overrideValue.HasValue
            ? new BmsLibraryInitializationService(overrideValue.Value)
            : new BmsLibraryInitializationService();
        return service.ApplyFileScanDiff(
            new BmsLibraryDbGateway(songDbPath),
            new BmsLibraryOptionsSnapshot(),
            Array.Empty<BMSFile>(),
            new BmsScanExecutionResult
            {
                Success = true,
                Result = CreateScanResult(Array.Empty<string>(), new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
            },
            0L,
            () => null,
            null,
            currentBmsonSongs: Array.Empty<LR2SongDBExtended.bmson_song>());
    }

    private static LR2SongDBExtended.chart_info CreateMinimalChartInfoRow(string sha256, string md5)
    {
        return new LR2SongDBExtended.chart_info
        {
            sha256 = sha256,
            md5 = md5,
            charthash = sha256,
            level = 7,
            difficulty = 4,
            difficulty_defined = true,
            mainbpm = 120,
            maxbpm = 120,
            minbpm = 120,
            length = 1000,
            mode = 7,
            judge = 100,
            feature = 0,
            notes = 1,
            n = 1,
            ln = 0,
            s = 0,
            ls = 0,
            total = 100,
            total_defined = true,
            density = 1,
            peakdensity = 1,
            enddensity = 1,
            distribution = string.Empty,
            speedchange = string.Empty,
            speedchange_count = 0,
            lanenotes = string.Empty,
            parser_version = BmsLibraryDbGateway.CurrentChartInfoParserVersion,
            updated_at = DateTime.UtcNow
        };
    }

    private static LR2SongDBExtended.chart_info_parse_failure CreateChartInfoParseFailureRow(string md5, string sha256, string path)
    {
        return new LR2SongDBExtended.chart_info_parse_failure
        {
            md5 = md5,
            sha256 = sha256,
            path = path,
            parser_version = BmsLibraryDbGateway.CurrentChartInfoParserVersion,
            failure_kind = "parse_failed",
            exception_type = "InvalidDataException",
            message = "bad bpm",
            parse_timeout_ms = null,
            updated_at = DateTime.UtcNow
        };
    }

    private static BmsScanResult CreateScanResult(IEnumerable<string> chartPaths, IDictionary<string, IEnumerable<string>> resourcesByDirectory)
    {
        HashSet<string> chartPathSet = new HashSet<string>(chartPaths ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        HashSet<string> chartDirectories = new HashSet<string>(
            chartPathSet.Select(Path.GetDirectoryName).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        foreach (string directoryPath in resourcesByDirectory?.Keys ?? Enumerable.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                chartDirectories.Add(directoryPath);
            }
        }

        BmsScanResult result = new BmsScanResult
        {
            ChartFilePaths = chartPathSet,
            ChartDirectories = chartDirectories
        };
        DirectoryResourceLookupCache cache = new DirectoryResourceLookupCache();
        foreach (string chartDirectory in chartDirectories)
        {
            IEnumerable<string>? resourceFiles = null;
            resourcesByDirectory?.TryGetValue(chartDirectory, out resourceFiles);
            cache.AddDir(chartDirectory, resourceFiles ?? Array.Empty<string>());
            DirectoryResourceLookupCache.Entry entry = cache.GetEntryOrNull(chartDirectory) ?? new DirectoryResourceLookupCache.Entry();
            result.AllResourceBaseNameHashesByChartDirectory[chartDirectory] = entry.AllBaseNameHashArray;
            result.AudioBaseNameHashesByChartDirectory[chartDirectory] = entry.AudioBaseNameHashArray;
            result.ImageBaseNameHashesByChartDirectory[chartDirectory] = entry.ImageBaseNameHashArray;
            result.MovieBaseNameHashesByChartDirectory[chartDirectory] = entry.MovieBaseNameHashArray;
            result.AudioRelativePathHashesByChartDirectory[chartDirectory] = entry.AudioRelativePathHashArray;
            result.ImageRelativePathHashesByChartDirectory[chartDirectory] = entry.ImageRelativePathHashArray;
            result.MovieRelativePathHashesByChartDirectory[chartDirectory] = entry.MovieRelativePathHashArray;
            result.SelfOwnedAllResourceBaseNameHashesByChartDirectory[chartDirectory] = entry.SelfOwnedAllBaseNameHashArray;
            result.SelfOwnedAudioBaseNameHashesByChartDirectory[chartDirectory] = entry.SelfOwnedAudioBaseNameHashArray;
            result.SelfOwnedImageBaseNameHashesByChartDirectory[chartDirectory] = entry.SelfOwnedImageBaseNameHashArray;
            result.SelfOwnedMovieBaseNameHashesByChartDirectory[chartDirectory] = entry.SelfOwnedMovieBaseNameHashArray;
            result.SelfOwnedAudioRelativePathHashesByChartDirectory[chartDirectory] = entry.SelfOwnedAudioRelativePathHashArray;
            result.SelfOwnedImageRelativePathHashesByChartDirectory[chartDirectory] = entry.SelfOwnedImageRelativePathHashArray;
            result.SelfOwnedMovieRelativePathHashesByChartDirectory[chartDirectory] = entry.SelfOwnedMovieRelativePathHashArray;
        }
        return result;
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
