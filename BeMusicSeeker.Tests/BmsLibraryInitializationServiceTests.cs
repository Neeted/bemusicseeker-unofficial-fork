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
    public void LoadSongTable_FixesRelativePathsWithoutMaintenanceHydration()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string rootedChartPath = Path.Combine(lr2RootPath, "Songs", "chart.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(rootedChartPath));
            File.WriteAllText(rootedChartPath, "#PLAYER 1");

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();

                var song = new TestableBmsFile
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

            var service = new BmsLibraryInitializationService();
            SongTableLoadResult result = service.LoadSongTable(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                null,
                new TestFileMutationService(),
                null,
                ex => ex.Message);

            Assert.AreEqual(1, result.LoadedFiles.Count);
            Assert.AreEqual(rootedChartPath, result.LoadedFiles[0].path);
            Assert.IsNull(result.LoadedFiles[0].maintenanceInfo.encoding);
            Assert.AreEqual(1, result.RelativePathFixedCount);
            Assert.IsTrue(result.DbWriteRequired);
            CollectionAssert.Contains(result.DeletedSongPaths, Path.Combine("Songs", "chart.bms"));
            Assert.IsTrue(result.UpdatedSongs.Any(file => file.path == rootedChartPath));
        });
    }

    [TestMethod]
    public void LoadMaintenanceTable_LoadsMaintenanceMapAfterCatalogLoad()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string rootedChartPath = Path.Combine(lr2RootPath, "Songs", "chart.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(rootedChartPath));
            File.WriteAllText(rootedChartPath, "#PLAYER 1");

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.InsertOrReplace(new BMSFileMaintenanceInfo
                {
                    path = rootedChartPath,
                    hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    encoding = "shift_jis"
                }, typeof(LR2SongDBExtended.maintenance));
            }

            var service = new BmsLibraryInitializationService();
            MaintenanceTableHydrationResult result = service.LoadMaintenanceTable(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot());

            Assert.AreEqual(1L, result.MaintenanceTableCount);
            Assert.AreEqual(1, result.MaintenanceMap.Count);
            Assert.IsTrue(result.ReadOnly);
            Assert.AreEqual(0L, result.DbLockWaitMs);
            Assert.IsTrue(result.MaintenanceMap.TryGetValue(rootedChartPath, out BMSFileMaintenanceInfo info));
            Assert.AreEqual("shift_jis", info.encoding);
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public void LoadSongTable_RawCatalogLoaderPreservesSongColumns()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string previousMode = Environment.GetEnvironmentVariable("BMS_SONG_TABLE_LOAD_MODE");
        Environment.SetEnvironmentVariable("BMS_SONG_TABLE_LOAD_MODE", null);
        try
        {
            WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
            {
                string chartPath = Path.Combine(lr2RootPath, "Songs", "full-columns.bms");
                Directory.CreateDirectory(Path.GetDirectoryName(chartPath));
                File.WriteAllText(chartPath, "#PLAYER 1");

                var expectedCrc = new TestableBmsFile
                {
                    path = chartPath
                };
                Lr2SongFolderParentNormalizer.ApplyIfMissingOrInvalid(expectedCrc);

                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.CreateTable<LR2SongDB.song>();
                    songDb.CreateTable<LR2SongDB.folder>();
                    songDb.Execute(
                        "INSERT INTO song (hash, title, subtitle, artist, subartist, genre, tag, path, type, folder, stagefile, banner, backbmp, parent, level, difficulty, maxbpm, minbpm, mode, judge, longnote, bga, random, date, favorite, txt, karinotes, adddate, exlevel) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
                        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                        "Title",
                        "Subtitle",
                        "Artist",
                        "SubArtist",
                        "Genre",
                        "Tag",
                        chartPath,
                        1,
                        expectedCrc.folder,
                        "stage.png",
                        "banner.png",
                        "back.png",
                        expectedCrc.parent,
                        12,
                        4,
                        180,
                        90,
                        7,
                        2,
                        1,
                        1,
                        0,
                        12345,
                        1,
                        0,
                        678,
                        23456,
                        9);
                }

                var service = new BmsLibraryInitializationService();
                SongTableLoadResult result = service.LoadSongTable(
                    new BmsLibraryDbGateway(songDbPath),
                    new BmsLibraryOptionsSnapshot(),
                    null,
                    new TestFileMutationService(),
                    null,
                    ex => ex.Message);

                Assert.AreEqual("raw_string", result.SongMaterializeMode);
                Assert.AreEqual(1, result.SongRawRows);
                BMSFile loaded = result.LoadedFiles.Single();
                Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", loaded.hash);
                Assert.AreEqual("Title", loaded.title);
                Assert.AreEqual("Subtitle", loaded.subtitle);
                Assert.AreEqual("Artist", loaded.artist);
                Assert.AreEqual("SubArtist", loaded.subartist);
                Assert.AreEqual("Genre", loaded.genre);
                Assert.AreEqual("Tag", loaded.tag);
                Assert.AreEqual(chartPath, loaded.path);
                Assert.AreEqual(1, loaded.type);
                Assert.AreEqual(expectedCrc.folder, loaded.folder);
                Assert.AreEqual("stage.png", loaded.stagefile);
                Assert.AreEqual("banner.png", loaded.banner);
                Assert.AreEqual("back.png", loaded.backbmp);
                Assert.AreEqual(expectedCrc.parent, loaded.parent);
                Assert.AreEqual(12, loaded.level);
                Assert.AreEqual(4, loaded.difficulty);
                Assert.AreEqual(180, loaded.maxbpm);
                Assert.AreEqual(90, loaded.minbpm);
                Assert.AreEqual(7, loaded.mode);
                Assert.AreEqual(2, loaded.judge);
                Assert.AreEqual(1, loaded.longnote);
                Assert.AreEqual(1, loaded.bga);
                Assert.AreEqual(0, loaded.random);
                Assert.AreEqual(12345, loaded.date);
                Assert.AreEqual(1, loaded.favorite);
                Assert.AreEqual(0, loaded.txt);
                Assert.AreEqual(678, loaded.karinotes);
                Assert.AreEqual(23456, loaded.adddate);
                Assert.AreEqual(9, loaded.exlevel);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("BMS_SONG_TABLE_LOAD_MODE", previousMode);
        }
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

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();

                var song = new TestableBmsFile
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

            var service = new BmsLibraryInitializationService();
            SongTableLoadResult result = service.LoadSongTable(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                null,
                new TestFileMutationService(),
                null,
                ex => ex.Message);
            List<string> propertyNames = [];
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

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);

                var song = new TestableBmsFile
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

            var service = new BmsLibraryInitializationService();
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

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                var song = new TestableBmsFile
                {
                    path = chartPath,
                    folder = "folder",
                    parent = "parent"
                };
                song.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                songDb.InsertOrReplace(song, typeof(LR2SongDB.song));
            }

            var service = new BmsLibraryInitializationService();
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

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", chartPath));
            Assert.IsTrue(string.IsNullOrWhiteSpace(verify.ExecuteScalar<string>("SELECT parent FROM song WHERE path = ?;", chartPath)));
        });
    }

    [TestMethod]
    public void LoadSongTable_StandalonePreservesShiftJisUnsupportedExistingSongAndWarns()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryStandaloneSongDb(delegate (string rootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(rootPath, "Songs😀");
            Directory.CreateDirectory(chartDirectoryPath);
            string chartPath = Path.Combine(chartDirectoryPath, "chart.bms");
            File.WriteAllText(chartPath, CreateValidBmsText("Standalone Emoji Path"), Encoding.ASCII);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                var song = new TestableBmsFile
                {
                    path = chartPath,
                    folder = "folder",
                    parent = "parent"
                };
                song.SetHash("abababababababababababababababab");
                songDb.InsertOrReplace(song, typeof(LR2SongDB.song));
            }

            var service = new BmsLibraryInitializationService();
            SongTableLoadResult result = service.LoadSongTable(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false },
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

            using var verify = new LR2SongDBExtended(songDbPath);
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

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                var song = new TestableBmsFile
                {
                    path = relativePath,
                    folder = "folder",
                    parent = "parent"
                };
                song.SetHash("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
                songDb.InsertOrReplace(song, typeof(LR2SongDB.song));
            }

            var service = new BmsLibraryInitializationService();
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

            using var verify = new LR2SongDBExtended(songDbPath);
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

            var keepFile = new TestableBmsFile
            {
                path = Path.Combine(keepDirectoryPath, "keep.bms")
            };
            keepFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var deletedFile = new TestableBmsFile
            {
                path = Path.Combine(lr2RootPath, "Deleted", "deleted.bms")
            };
            deletedFile.SetHash("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            int executeScanCount = 0;
            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [keepFile, deletedFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    NativeBridgeUsed = true,
                    NativeBridgeMs = 234L,
                    NativeBridgeReason = "everything_bridge_fixed_scan",
                    ManagedDecodeMs = 12L,
                    ManagedMaterializeMs = 7L,
                    BridgeRawBufferBytes = 4096UL,
                    Result = CreateScanResult(
                        [keepFile.path, Path.Combine(newDirectoryPath, "added.bms")],
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
                null,
                currentInstallDestinationCharts: [ChartFileProjection.WithPackageState(
                    ChartFileProjection.FromBmsFile(keepFile, includeWarningSnapshot: false),
                    staleDirectoryPath,
                    string.Empty,
                    string.Empty,
                    [])]);

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
            LibraryInstallDestinationChange installDestinationChange = result.MutationDelta.UpdatedInstallDestinations.Single();
            Assert.AreSame(keepFile, installDestinationChange.Chart.GetBmsStorageOwner());
            Assert.IsNull(installDestinationChange.NewInstallDestination);
            Assert.IsTrue(installDestinationChange.ClearInstallDestinationState);
            Assert.IsTrue(string.IsNullOrWhiteSpace(keepFile.instl_dst));
            CollectionAssert.Contains(result.NextDirectoryResourceLookupCache.Keys.ToList(), keepDirectoryPath);
            CollectionAssert.Contains(result.NextDirectoryResourceLookupCache.Keys.ToList(), newDirectoryPath);

            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.song>();
            List<BMSFile> dbFiles = [.. songDb.Table<BMSFile>()];
            Assert.AreEqual(1, dbFiles.Count);
            Assert.AreEqual(Path.Combine(newDirectoryPath, "added.bms"), dbFiles[0].path);
            List<LR2SongDBExtended.chart_digest_map> digestRows = [.. songDb.Table<LR2SongDBExtended.chart_digest_map>()];
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

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDbConnection);
            }

            int scanCompletedCount = 0;
            int fileDiffStartedCount = 0;
            object progressLock = new();
            List<(int Total, int Processed, string Path)> progress = [];
            List<string> logs = [];
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath, bmsonPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                logInstallPerformance: message => logs.Add(message),
                currentBmsonSongs: [],
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
            Assert.AreEqual(1, result.DbCommitChunks);
            Assert.AreEqual(10000, result.DbCommitChunkSize);
            Assert.IsTrue(result.DbCommitMaxChunkMs >= 0);
            Assert.IsTrue(result.FileDiffReadMs >= 0);
            Assert.IsTrue(result.FileDiffParseMs >= 0);
            Assert.IsTrue(result.SnapshotQueueHighWatermark > 0);
            Assert.AreEqual(2048, result.InlineChartInfoBatchSize);
            Assert.AreEqual(2, result.ReadQueueCapacity);
            Assert.AreEqual(2048, result.ParsedQueueCapacity);
            Assert.AreEqual(1, result.PostParseQueueCapacity);
            Assert.AreEqual(0, result.CommitQueueCapacity);
            Assert.AreEqual(1, result.PostParseBatchCount);
            Assert.AreEqual(1, result.InlineMaintenanceDegree);
            Assert.IsTrue(result.PostParseWallMs >= 0);
            Assert.IsTrue(result.InlineChartInfoWallMs >= 0);
            Assert.IsTrue(result.InlineMaintenanceWallMs >= 0);
            Assert.IsTrue(progress.Any(item => item.Total == 2 && item.Processed == 0));
            Assert.IsTrue(progress.Any(item => item.Total == 2 && item.Processed == 2));
            Assert.IsTrue(progress.All(item => item.Total == 2));
            Assert.IsTrue(logs.Any(message => message.Contains("bms_added_target_count=1")
                && message.Contains("bmson_upsert_target_count=1")
                && message.Contains("file_diff_parser_degree=1")
                && message.Contains("read_queue_capacity=2")
                && message.Contains("parsed_queue_capacity=2048")
                && message.Contains("post_parse_batch_count=1")
                && message.Contains("inline_chart_info_target_count=2")
                && message.Contains("inline_chart_info_batch_size=2048")
                && message.Contains("inline_maintenance_degree=1")
                && message.Contains("parse_read_bytes_estimate=" + expectedReadBytesEstimate)
                && message.Contains("db_commit_chunks=1")
                && message.Contains("db_commit_chunk_size=10000")));
            Assert.IsTrue(logs.Any(message => message.Contains("song_tbl_file_check db_commit_chunk_done chunk=1")));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_CommitsChunksThroughPostParseWriter()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "ManyAdded");
            Directory.CreateDirectory(chartDirectoryPath);
            List<string> paths = [];
            for (int i = 0; i < 120; i++)
            {
                string path = Path.Combine(chartDirectoryPath, "added-" + i.ToString("D4") + ".bms");
                File.WriteAllText(path, "#PLAYER 1\r\n#TITLE Added " + i.ToString("D4") + "\r\n", Encoding.ASCII);
                paths.Add(path);
            }

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            List<string> events = [];
            object eventLock = new();
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1, inlineChartInfoBatchSizeOverride: 32, fileDiffCommitChunkSizeOverride: 50);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        paths,
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                logInstallPerformance: delegate (string message)
                {
                    lock (eventLock)
                    {
                        if (message.Contains("song_tbl_file_check db_commit_chunk_done"))
                        {
                            events.Add(message);
                        }
                    }
                },
                reportParseProgress: delegate (int total, int processed, string path)
                {
                    lock (eventLock)
                    {
                        events.Add("progress " + processed + "/" + total);
                    }
                });

            Assert.AreEqual(120, result.AddedFiles.Count);
            Assert.AreEqual(2, result.DbCommitChunks);
            Assert.AreEqual(50, result.DbCommitChunkSize);
            Assert.AreEqual(32, result.InlineChartInfoBatchSize);
            Assert.IsTrue(result.PostParseBatchCount >= 3);
            Assert.IsTrue(result.PostParseWallMs >= 0);
            Assert.IsTrue(result.CommitQueueWaitMs >= 0);
            int firstCommitIndex = events.FindIndex(item => item.Contains("db_commit_chunk_done chunk=1"));
            Assert.IsTrue(firstCommitIndex >= 0, "first commit chunk log was not recorded.");
            Assert.IsTrue(events.Any(item => item == "progress 120/120"), "final parse progress was not recorded.");
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(120L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song;"));
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

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: []);

            Assert.AreEqual(1, result.AddedFiles.Count);
            BMSFile added = result.AddedFiles[0];
            Assert.IsFalse(string.IsNullOrWhiteSpace(added.folder));
            Assert.IsFalse(string.IsNullOrWhiteSpace(added.parent));
            Assert.IsTrue(added.parent.Length <= 8);
            Assert.IsFalse(added.Warnings.Contains(ChartWarningKind.Lr2PathEncodingUnsupported));
            Assert.AreEqual(0, result.NextFiles.Count(file => string.IsNullOrWhiteSpace(file.parent)));

            using var verify = new LR2SongDBExtended(songDbPath);
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

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: []);

            Assert.AreEqual(1, result.AddedFiles.Count);
            BMSFile added = result.AddedFiles[0];
            Assert.IsTrue(string.IsNullOrWhiteSpace(added.folder));
            Assert.IsTrue(string.IsNullOrWhiteSpace(added.parent));
            Assert.IsTrue(added.Warnings.Contains(ChartWarningKind.Lr2PathEncodingUnsupported));
            StringAssert.Contains(added.Warnings.BuildTooltipText(), "Shift_JIS");
            Assert.AreEqual(1, result.NextFiles.Count(file => string.IsNullOrWhiteSpace(file.parent)));

            using var verify = new LR2SongDBExtended(songDbPath);
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
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            var file = new TestableBmsFile
            {
                path = bmsPath,
                folder = null,
                parent = null
            };
            file.SetHash("cccccccccccccccccccccccccccccccc");

            new BmsLibraryDbGateway(songDbPath).UpsertSongs([file]);

            Assert.IsFalse(string.IsNullOrWhiteSpace(file.folder));
            Assert.IsFalse(string.IsNullOrWhiteSpace(file.parent));
            using var verify = new LR2SongDBExtended(songDbPath);
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
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            var file = new TestableBmsFile
            {
                path = bmsPath,
                folder = null,
                parent = null
            };
            file.SetHash("dddddddddddddddddddddddddddddddd");

            new BmsLibraryDbGateway(songDbPath).UpsertSongs([file]);

            Assert.IsTrue(string.IsNullOrWhiteSpace(file.parent));
            Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.Lr2PathEncodingUnsupported));
            using var verify = new LR2SongDBExtended(songDbPath);
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

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: []);

            Assert.AreEqual(1, result.AddedFiles.Count);
            Assert.AreEqual(1, result.InlineChartInfoTargetCount);
            Assert.AreEqual(1, result.InlineChartInfoSuccessCount);
            Assert.AreEqual(0, result.InlineChartInfoParseFailedCount);
            Assert.AreEqual(1, result.InlineChartInfoRows.Count);
            Assert.AreEqual(1, result.InlineChartInfoAppliedRows.Count);
            Assert.IsNotNull(result.AddedFiles[0].ChartInfo);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ? AND md5 = ?;", result.AddedFiles[0].sha256, result.AddedFiles[0].hash));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_parse_failure;"));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_AddsBmsPersistsInlineMaintenanceAndClearsResourceRefs()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "InlineMaintenance");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "added.bms");
            string wavPath = Path.Combine(chartDirectoryPath, "sound.wav");
            File.WriteAllText(bmsPath, CreateValidBmsText("Inline Maintenance"), Encoding.ASCII);
            File.WriteAllBytes(wavPath, [1, 2, 3]);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, new[] { "sound.wav" } }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: []);

            BMSFile added = result.AddedFiles.Single();
            Assert.AreEqual(1, result.InlineMaintenanceTargetCount);
            Assert.AreEqual(1, result.InlineMaintenanceSuccessCount);
            Assert.AreEqual(0, result.InlineMaintenanceFailedCount);
            Assert.AreEqual(1, result.InlineMaintenanceBmsCount);
            Assert.IsNull(added.WAVfiles);
            Assert.IsNull(added.BGAfiles);
            Assert.AreEqual(1, added.maintenanceInfo.wav_files_defined);
            Assert.AreEqual(1, added.maintenanceInfo.wav_files_existing);

            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDBExtended.maintenance maintenance = verify.Query<LR2SongDBExtended.maintenance>("SELECT * FROM maintenance WHERE path = ?;", bmsPath).Single();
            Assert.AreEqual(added.hash, maintenance.hash);
            Assert.AreEqual(1, maintenance.wav_files_defined);
            Assert.AreEqual(1, maintenance.wav_files_existing);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_ReloadsDetectedEncodingDuringInlineMaintenance()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "InlineEncoding");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "added.bms");
            string title = "\uacaf";
            string subtitle = "[Pattern]";
            string artist = "KoreanArtist";
            string subartist = "obj:Tester";
            File.WriteAllText(
                bmsPath,
                "#PLAYER 1\r\n#TITLE " + title + "\r\n#SUBTITLE " + subtitle + "\r\n#ARTIST " + artist + "\r\n#SUBARTIST " + subartist + "\r\n#WAV01 sound.wav\r\n#00111:01\r\n",
                Encoding.GetEncoding("ks_c_5601-1987"));

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, new[] { "sound.wav" } }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: []);

            BMSFile added = result.AddedFiles.Single();
            Assert.AreEqual(title, added.title);
            Assert.AreEqual(subtitle, added.subtitle);
            Assert.AreEqual(title + " " + subtitle, added.Title);
            Assert.AreEqual(artist, added.artist);
            Assert.AreEqual(subartist, added.subartist);
            Assert.AreEqual(artist + " " + subartist, added.Artist);
            Assert.AreEqual("ks_c_5601-1987", added.maintenanceInfo.encoding);
            Assert.IsTrue(added.maintenanceInfo.is_encoding_fixed);
            Assert.AreEqual(1, result.InlineEncodingReloadCount);
            Assert.IsTrue(result.InlineEncodingReloadWallMs >= 0);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(title, verify.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", bmsPath));
            Assert.AreEqual(subtitle, verify.ExecuteScalar<string>("SELECT subtitle FROM song WHERE path = ?;", bmsPath));
            Assert.AreEqual(artist, verify.ExecuteScalar<string>("SELECT artist FROM song WHERE path = ?;", bmsPath));
            Assert.AreEqual(subartist, verify.ExecuteScalar<string>("SELECT subartist FROM song WHERE path = ?;", bmsPath));
            LR2SongDBExtended.maintenance maintenance = verify.Query<LR2SongDBExtended.maintenance>("SELECT * FROM maintenance WHERE path = ?;", bmsPath).Single();
            Assert.AreEqual("ks_c_5601-1987", maintenance.encoding);
            Assert.AreEqual(1, maintenance.wav_files_defined);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_TracksInlineEncodingOutcomesForMixedBmsBatch()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "InlineEncodingMixed");
            Directory.CreateDirectory(chartDirectoryPath);
            string asciiPath = Path.Combine(chartDirectoryPath, "ascii.bms");
            string koreanPath = Path.Combine(chartDirectoryPath, "korean.bms");
            const string koreanTitle = "\uacaf";
            File.WriteAllText(asciiPath, CreateValidBmsText("ASCII"), Encoding.ASCII);
            File.WriteAllText(
                koreanPath,
                "#PLAYER 1\r\n#TITLE " + koreanTitle + "\r\n#ARTIST KoreanArtist\r\n#BPM 120\r\n#WAV01 sound.wav\r\n#00111:01\r\n",
                Encoding.GetEncoding("ks_c_5601-1987"));
            File.WriteAllBytes(Path.Combine(chartDirectoryPath, "sound.wav"), [1, 2, 3]);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [asciiPath, koreanPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, new[] { "sound.wav" } }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: []);

            Assert.AreEqual(2, result.AddedFiles.Count);
            Assert.AreEqual(2, result.InlineEncodingDetectCount);
            Assert.AreEqual(1, result.InlineEncodingFastAsciiCount);
            Assert.AreEqual(1, result.InlineEncodingShiftJisCount);
            Assert.AreEqual(1, result.InlineEncodingKoreanCount);
            Assert.AreEqual(0, result.InlineEncodingShiftJisQuestionCount);
            Assert.AreEqual(0, result.InlineEncodingKoreanQuestionCount);
            Assert.AreEqual(0, result.InlineEncodingUtf8Count);
            Assert.AreEqual(0, result.InlineEncodingUnknownCount);
            Assert.AreEqual(0, result.InlineEncodingOtherCount);
            Assert.AreEqual(1, result.InlineEncodingReloadCount);
            Assert.IsTrue(result.InlineEncodingMaxItemMs >= 0);

            BMSFile ascii = result.AddedFiles.Single(file => file.path == asciiPath);
            BMSFile korean = result.AddedFiles.Single(file => file.path == koreanPath);
            Assert.AreEqual("shift_jis", ascii.maintenanceInfo.encoding);
            Assert.IsFalse(ascii.maintenanceInfo.is_encoding_fixed);
            Assert.AreEqual("ks_c_5601-1987", korean.maintenanceInfo.encoding);
            Assert.IsTrue(korean.maintenanceInfo.is_encoding_fixed);
            Assert.AreEqual(koreanTitle, korean.Title);
            Assert.IsNull(ascii.WAVfiles);
            Assert.IsNull(korean.WAVfiles);
            Assert.AreEqual(1, ascii.maintenanceInfo.wav_files_existing);
            Assert.AreEqual(1, korean.maintenanceInfo.wav_files_existing);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(koreanTitle, verify.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", koreanPath));
            Assert.AreEqual("ks_c_5601-1987", verify.ExecuteScalar<string>("SELECT encoding FROM maintenance WHERE path = ?;", koreanPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_CachesFolderParentHashesForChartsInSameDirectory()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "FolderParentCache");
            Directory.CreateDirectory(chartDirectoryPath);
            string firstPath = Path.Combine(chartDirectoryPath, "first.bms");
            string secondPath = Path.Combine(chartDirectoryPath, "second.bms");
            string wavPath = Path.Combine(chartDirectoryPath, "sound.wav");
            File.WriteAllText(firstPath, CreateValidBmsText("First"), Encoding.ASCII);
            File.WriteAllText(secondPath, CreateValidBmsText("Second"), Encoding.ASCII);
            File.WriteAllBytes(wavPath, [1, 2, 3]);

            var expected = BMSFile.CreateBMSFileFromFile(firstPath);
            Lr2SongFolderParentNormalizer.ApplyIfMissingOrInvalid(expected);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 2);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [firstPath, secondPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, new[] { "sound.wav" } }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: []);

            Assert.AreEqual(2, result.AddedFiles.Count);
            Assert.IsTrue(result.InlineHealthWallMs >= 0);
            Assert.IsTrue(result.InlineEncodingWallMs >= 0);
            Assert.IsTrue(result.InlineBmsMaintenanceWallMs >= 0);
            foreach (BMSFile added in result.AddedFiles)
            {
                Assert.AreEqual(expected.folder, added.folder);
                Assert.AreEqual(expected.parent, added.parent);
                Assert.IsFalse(added.Warnings.Contains(ChartWarningKind.Lr2PathEncodingUnsupported));
                Assert.AreEqual(1, added.maintenanceInfo.wav_files_defined);
                Assert.AreEqual(1, added.maintenanceInfo.wav_files_existing);
            }
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_DeletesBmsAndMaintenanceInSameChunk()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "DeleteMaintenance");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "deleted.bms");

            var existing = new TestableBmsFile
            {
                path = bmsPath
            };
            existing.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDBExtended.maintenance>();
                songDbConnection.InsertOrReplace(existing, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(new BMSFileMaintenanceInfo
                {
                    path = bmsPath,
                    hash = existing.hash,
                    encoding = "shift_jis"
                }, typeof(LR2SongDBExtended.maintenance));
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [existing],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: []);

            Assert.AreEqual(1, result.DeletedPaths.Count);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", bmsPath));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM maintenance WHERE path = ?;", bmsPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_BulkDeleteKeepsExactPathKeys()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsPath = " " + Path.Combine(lr2RootPath, "Whitespace", "deleted.bms");

            var existing = new TestableBmsFile
            {
                path = bmsPath
            };
            existing.SetHash("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDBExtended.maintenance>();
                songDbConnection.InsertOrReplace(existing, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(new BMSFileMaintenanceInfo
                {
                    path = bmsPath,
                    hash = existing.hash,
                    encoding = "shift_jis"
                }, typeof(LR2SongDBExtended.maintenance));
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [existing],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: []);

            Assert.AreEqual(1, result.DeletedPaths.Count);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", bmsPath));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM maintenance WHERE path = ?;", bmsPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_BulkDeleteHandlesMultipleCommitChunks()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            TestableBmsFile[] bmsFiles = [.. Enumerable.Range(0, 3)
                .Select(delegate (int index)
                {
                    var file = new TestableBmsFile
                    {
                        path = Path.Combine(lr2RootPath, "BulkDelete", "deleted" + index + ".bms")
                    };
                    file.SetHash(new string((char)('a' + index), 32));
                    return file;
                })];
            LR2SongDBExtended.bmson_song[] bmsonSongs = [.. Enumerable.Range(0, 2)
                .Select(index => new LR2SongDBExtended.bmson_song
                {
                    path = Path.Combine(lr2RootPath, "BulkDelete", "deleted" + index + ".bmson"),
                    folder = Path.Combine(lr2RootPath, "BulkDelete"),
                    title = "Deleted " + index,
                    md5 = new string((char)('d' + index), 32),
                    sha256 = new string(index == 0 ? '1' : '2', 64),
                    updated_at = DateTime.UtcNow.AddDays(-1)
                })];

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDbConnection);
                songDbConnection.CreateTable<LR2SongDBExtended.maintenance>();
                foreach (TestableBmsFile file in bmsFiles)
                {
                    songDbConnection.InsertOrReplace(file, typeof(LR2SongDB.song));
                    songDbConnection.InsertOrReplace(new BMSFileMaintenanceInfo
                    {
                        path = file.path,
                        hash = file.hash,
                        encoding = "shift_jis"
                    }, typeof(LR2SongDBExtended.maintenance));
                }
                foreach (LR2SongDBExtended.bmson_song song in bmsonSongs)
                {
                    songDbConnection.InsertOrReplace(song, typeof(LR2SongDBExtended.bmson_song));
                    songDbConnection.InsertOrReplace(new BMSFileMaintenanceInfo
                    {
                        path = song.path,
                        hash = song.md5,
                        encoding = "utf-8"
                    }, typeof(LR2SongDBExtended.maintenance));
                }
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1, fileDiffCommitChunkSizeOverride: 2);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                bmsFiles,
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: bmsonSongs,
                executeBmsonScan: () => new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                });

            Assert.AreEqual(3, result.DeletedPaths.Count);
            Assert.AreEqual(2, result.DeletedBmsonPaths.Count);
            Assert.AreEqual(3, result.DbCommitChunks);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song;"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM bmson_song;"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM maintenance;"));
        });
    }

    [TestMethod]
    public void EnsureSongLookupIndexes_CreatesLr2CompatibleHashAndParentIndexes()
    {
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                BmsLibraryDbGateway.EnsureSongLookupIndexes(songDb);
            }

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'hashidx';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'parentidx';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'song_idx_folder';"));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_AddsBmsPublishesGeneratedInlineChartInfoToCallback()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "InlineInfoCallback");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "added.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Inline Added Callback"), Encoding.ASCII);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            List<LR2SongDBExtended.chart_info> callbackRows = [];
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [],
                inlineChartInfoRowsCommitted: rows => callbackRows.AddRange(rows));

            Assert.AreEqual(1, result.AddedFiles.Count);
            Assert.AreEqual(1, result.InlineChartInfoTargetCount);
            Assert.AreEqual(1, result.InlineChartInfoSuccessCount);
            Assert.AreEqual(1, result.InlineChartInfoIndexPublishedCount);
            Assert.AreEqual(0, result.InlineChartInfoRows.Count);
            Assert.AreEqual(0, result.InlineChartInfoAppliedRows.Count);
            Assert.AreEqual(1, callbackRows.Count);
            Assert.AreEqual(result.AddedFiles[0].sha256, callbackRows[0].sha256);
            Assert.IsNotNull(result.AddedFiles[0].ChartInfo);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ? AND md5 = ?;", result.AddedFiles[0].sha256, result.AddedFiles[0].hash));
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

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: []);

            Assert.AreEqual(1, result.AddedFiles.Count);
            Assert.AreEqual(1, result.InlineChartInfoTargetCount);
            Assert.AreEqual(1, result.InlineChartInfoParseFailedCount);
            Assert.AreEqual(1, result.InlineChartInfoFailurePersistedCount);
            Assert.AreEqual(0, result.InlineChartInfoRows.Count);

            using var verify = new LR2SongDBExtended(songDbPath);
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

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDbConnection);
                songDbConnection.InsertOrReplace(
                    CreateChartInfoParseFailureRow(snapshot.Md5, snapshot.Sha256, bmsPath),
                    typeof(LR2SongDBExtended.chart_info_parse_failure));
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: []);

            Assert.AreEqual(1, result.AddedFiles.Count);
            Assert.AreEqual(1, result.InlineChartInfoTargetCount);
            Assert.AreEqual(1, result.InlineChartInfoFailureSkippedCount);
            Assert.AreEqual(0, result.InlineChartInfoSuccessCount);
            Assert.AreEqual(0, result.InlineChartInfoRows.Count);
            Assert.IsNull(result.AddedFiles[0].ChartInfo);

            using var verify = new LR2SongDBExtended(songDbPath);
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

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertChartInfos([CreateMinimalChartInfoRow(snapshot.Sha256, snapshot.Md5)]);

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                gateway,
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: []);

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
    public void ApplyFileScanDiff_CurrentInlineChartInfoRowsDoNotPublishToCallback()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "InlineCurrentCallback");
            Directory.CreateDirectory(chartDirectoryPath);
            string firstPath = Path.Combine(chartDirectoryPath, "current1.bms");
            string secondPath = Path.Combine(chartDirectoryPath, "current2.bms");
            File.WriteAllText(firstPath, CreateValidBmsText("Inline Current 1"), Encoding.ASCII);
            File.WriteAllText(secondPath, CreateValidBmsText("Inline Current 2"), Encoding.ASCII);
            ChartFileSnapshot firstSnapshot = ChartFileContentReader.ReadSnapshot(firstPath);
            ChartFileSnapshot secondSnapshot = ChartFileContentReader.ReadSnapshot(secondPath);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertChartInfos(
            [
                CreateMinimalChartInfoRow(firstSnapshot.Sha256, firstSnapshot.Md5),
                CreateMinimalChartInfoRow(secondSnapshot.Sha256, secondSnapshot.Md5)
            ]);

            List<LR2SongDBExtended.chart_info> callbackRows = [];
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                gateway,
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [firstPath, secondPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [],
                inlineChartInfoRowsCommitted: rows => callbackRows.AddRange(rows));

            Assert.AreEqual(2, result.AddedFiles.Count);
            Assert.AreEqual(2, result.InlineChartInfoTargetCount);
            Assert.AreEqual(2, result.InlineChartInfoCurrentSkippedCount);
            Assert.AreEqual(0, result.InlineChartInfoSuccessCount);
            Assert.AreEqual(0, result.InlineChartInfoIndexPublishedCount);
            Assert.AreEqual(0, callbackRows.Count);
            Assert.IsTrue(result.AddedFiles.All(file => file.ChartInfo != null));
            Assert.IsTrue(result.AddedFiles.All(file => file.ChartInfo.level == 7));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_FileDiffParserDegree_UsesDefaultAndNormalizesOverrides()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            int expectedDefault = Math.Max(1, Environment.ProcessorCount - 1);
            Assert.AreEqual(expectedDefault, BmsLibraryInitializationService.ResolveDefaultFileDiffParserDegree());

            Assert.AreEqual(expectedDefault, RunWithParserDegreeOverride(null, songDbPath).FileDiffParserDegree);
            Assert.AreEqual(1, RunWithParserDegreeOverride(0, songDbPath).FileDiffParserDegree);
            Assert.AreEqual(1, RunWithParserDegreeOverride(-10, songDbPath).FileDiffParserDegree);
            Assert.AreEqual(2, RunWithParserDegreeOverride(2, songDbPath).FileDiffParserDegree);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_FileDiffCommitChunkSize_UsesDefaultAndNormalizesOverrides()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            Assert.AreEqual(10000, BmsLibraryInitializationService.ResolveDefaultFileDiffCommitChunkSize());

            Assert.AreEqual(10000, RunWithCommitChunkSizeOverride(null, songDbPath).DbCommitChunkSize);
            Assert.AreEqual(1, RunWithCommitChunkSizeOverride(0, songDbPath).DbCommitChunkSize);
            Assert.AreEqual(1, RunWithCommitChunkSizeOverride(-10, songDbPath).DbCommitChunkSize);
            Assert.AreEqual(2, RunWithCommitChunkSizeOverride(2, songDbPath).DbCommitChunkSize);
            Assert.AreEqual(1000, RunWithCommitChunkSizeOverride(1000, songDbPath).DbCommitChunkSize);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_InlineChartInfoBatchSize_UsesDefaultAndNormalizesOverrides()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            Assert.AreEqual(2048, BmsLibraryInitializationService.ResolveDefaultInlineChartInfoBatchSize());

            Assert.AreEqual(2048, RunWithInlineChartInfoBatchSizeOverride(null, songDbPath).InlineChartInfoBatchSize);
            Assert.AreEqual(1, RunWithInlineChartInfoBatchSizeOverride(0, songDbPath).InlineChartInfoBatchSize);
            Assert.AreEqual(1, RunWithInlineChartInfoBatchSizeOverride(-10, songDbPath).InlineChartInfoBatchSize);
            Assert.AreEqual(2, RunWithInlineChartInfoBatchSizeOverride(2, songDbPath).InlineChartInfoBatchSize);
            Assert.AreEqual(512, RunWithInlineChartInfoBatchSizeOverride(512, songDbPath).InlineChartInfoBatchSize);
        });
    }

    [TestMethod]
    public void SQLiteConnectionEx_ReadOptimizedPragmas_AppliesAndReportsState()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), "bemusicseeker-pragmas-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var disabled = new SQLiteConnectionEx(dbPath))
            {
                List<string> disabledLogs = disabled.TryApplyReadOptimizedPragmas(enabled: false);
                CollectionAssert.AreEqual(new[] { "enabled=false" }, disabledLogs);
            }

            using var enabled = new SQLiteConnectionEx(dbPath);
            List<string> enabledLogs = enabled.TryApplyReadOptimizedPragmas(enabled: true);
            Assert.AreEqual(3, enabledLogs.Count);
            Assert.AreEqual("temp_store=MEMORY:ok", enabledLogs[0]);
            Assert.AreEqual("cache_size=-262144:ok", enabledLogs[1]);
            StringAssert.StartsWith(enabledLogs[2], "mmap_size=2147483648:ok(");
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    [TestMethod]
    public void SongTableFileCheckResult_ReleasePostApplyTransientBuffers_ClearsTransientListsOnly()
    {
        var result = new SongTableFileCheckResult();
        var added = new BMSFile();
        var addedBmson = new LR2SongDBExtended.bmson_song();
        var next = new BMSFile();
        var nextBmson = new LR2SongDBExtended.bmson_song();
        result.Pragmas.Add("pragma");
        result.AddedFiles.Add(added);
        result.AddedBmsonSongs.Add(addedBmson);
        result.InlineChartInfoRows.Add(new LR2SongDBExtended.chart_info());
        result.InlineChartInfoAppliedRows.Add(new LR2SongDBExtended.chart_info());
        result.InlineChartInfoParseFailureRows.Add(new LR2SongDBExtended.chart_info_parse_failure());
        result.InlineChartInfoParseFailureDeleteMd5s.Add("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        result.DeletedPaths.Add("deleted.bms");
        result.DeletedBmsonPaths.Add("deleted.bmson");
        result.MutationDelta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange());
        result.MutationDelta.RaiseLibraryChartsChanged = true;
        result.MutationDelta.InvalidateInstalledDirectoryIndex = true;
        result.NextFiles.Add(next);
        result.NextBmsonSongs.Add(nextBmson);
        result.NextDirectoryResourceLookupCache = new DirectoryResourceLookupCache();

        result.ReleasePostApplyTransientBuffers();

        Assert.AreEqual(0, result.Pragmas.Count);
        Assert.AreEqual(0, result.AddedFiles.Count);
        Assert.AreEqual(0, result.AddedBmsonSongs.Count);
        Assert.AreEqual(0, result.InlineChartInfoRows.Count);
        Assert.AreEqual(0, result.InlineChartInfoAppliedRows.Count);
        Assert.AreEqual(0, result.InlineChartInfoParseFailureRows.Count);
        Assert.AreEqual(0, result.InlineChartInfoParseFailureDeleteMd5s.Count);
        Assert.AreEqual(0, result.DeletedPaths.Count);
        Assert.AreEqual(0, result.DeletedBmsonPaths.Count);
        Assert.AreEqual(0, result.MutationDelta.UpdatedInstallDestinations.Count);
        Assert.IsFalse(result.MutationDelta.RaiseLibraryChartsChanged);
        Assert.IsFalse(result.MutationDelta.InvalidateInstalledDirectoryIndex);
        Assert.AreEqual(1, result.NextFiles.Count);
        Assert.AreSame(next, result.NextFiles[0]);
        Assert.AreEqual(1, result.NextBmsonSongs.Count);
        Assert.AreSame(nextBmson, result.NextBmsonSongs[0]);
        Assert.IsNotNull(result.NextDirectoryResourceLookupCache);
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

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDbConnection);
            }

            object progressLock = new();
            List<(int Total, int Processed, string Path)> progress = [];
            List<string> logs = [];
            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsonPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                logEverythingScan: message => logs.Add(message),
                currentBmsonSongs: [],
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
            Assert.IsTrue(logs.Any(message => message.Contains("bmson_parse_failed") && message.Contains("invalid.bmson")));
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

            var keepFile = new TestableBmsFile
            {
                path = chartPath
            };
            keepFile.SetHash(BMSFile.CreateBMSFileFromFile(chartPath).hash);

            uint audioRelativeHash = ChartResourceKeyHash.GetLookupHash("sound\\sound");
            uint imageRelativeHash = ChartResourceKeyHash.GetLookupHash("bg");

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [keepFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = new ChartScanResult
                    {
                        ChartFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { chartPath },
                        ChartDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { chartDirectoryPath },
                        AudioRelativePathHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, new[] { audioRelativeHash } }
                        },
                        ImageRelativePathHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, new[] { imageRelativeHash } }
                        },
                        MovieRelativePathHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<uint>() }
                        },
                        SelfOwnedAudioRelativePathHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, new[] { audioRelativeHash } }
                        },
                        SelfOwnedImageRelativePathHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, new[] { imageRelativeHash } }
                        },
                        SelfOwnedMovieRelativePathHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<uint>() }
                        }
                    }
                },
                0L,
                () => null,
                null);

            DirectoryResourceLookupCache.Entry entry = result.NextDirectoryResourceLookupCache.GetEntryOrNull(chartDirectoryPath);
            Assert.IsNotNull(entry);
            Assert.AreEqual(1, entry.AudioFileNameHashCount);
            Assert.AreEqual(1, entry.ImageFileNameHashCount);
            Assert.AreEqual(0, entry.MovieFileNameHashCount);
            Assert.IsTrue(entry.AudioRelativePathHashes.Contains(audioRelativeHash));
            Assert.IsTrue(entry.ImageRelativePathHashes.Contains(imageRelativeHash));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_UsesNativeResourceIndexWithoutMaterializingScanHashMaps()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "Keep");
            string chartPath = Path.Combine(chartDirectoryPath, "keep.bms");
            Directory.CreateDirectory(chartDirectoryPath);
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Keep\r\n");

            var keepFile = new TestableBmsFile
            {
                path = chartPath
            };
            keepFile.SetHash(BMSFile.CreateBMSFileFromFile(chartPath).hash);

            uint audioRelativeHash = ChartResourceKeyHash.GetLookupHash("sound\\sound");
            uint movieRelativeHash = ChartResourceKeyHash.GetLookupHash("movie");
            var nativeIndex = LibraryResourceIndex.CreateFromNativeCanonicalArrays(
                [chartDirectoryPath],
                [[audioRelativeHash]],
                [[]],
                [[movieRelativeHash]],
                [[audioRelativeHash]],
                [[]],
                [[movieRelativeHash]],
                new Dictionary<uint, string[]> { { audioRelativeHash, new[] { chartDirectoryPath } } },
                [],
                new Dictionary<uint, string[]> { { movieRelativeHash, new[] { chartDirectoryPath } } });

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [keepFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    NativeBridgeReason = EverythingNative.FixedScanNativeBridgeReason,
                    AudioResourceKeyHashCount = 1,
                    MovieResourceKeyHashCount = 1,
                    ResourceIndex = nativeIndex,
                    Result = new ChartScanResult
                    {
                        ChartFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { chartPath },
                        ChartDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { chartDirectoryPath }
                    }
                },
                0L,
                () => null,
                null);

            DirectoryResourceLookupCache.Entry entry = result.NextDirectoryResourceLookupCache.GetEntryOrNull(chartDirectoryPath);
            Assert.IsNotNull(entry);
            Assert.AreEqual(1, entry.AudioFileNameHashCount);
            Assert.AreEqual(0, entry.ImageFileNameHashCount);
            Assert.AreEqual(1, entry.MovieFileNameHashCount);
            Assert.IsTrue(entry.AudioRelativePathHashes.Contains(audioRelativeHash));
            Assert.IsTrue(entry.MovieRelativePathHashes.Contains(movieRelativeHash));
            Assert.AreEqual(1ul, result.AudioResourceKeyHashEntryCount);
            Assert.AreEqual(0ul, result.ImageResourceKeyHashEntryCount);
            Assert.AreEqual(1ul, result.MovieResourceKeyHashEntryCount);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_RemovesOrphanChartDigestRowsForDeletedSongs()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string deletedChartPath = Path.Combine(lr2RootPath, "Deleted", "deleted.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(deletedChartPath));
            File.WriteAllText(deletedChartPath, "#PLAYER 1\r\n#TITLE Deleted\r\n");

            var deletedFile = new TestableBmsFile
            {
                path = deletedChartPath
            };
            var source = BMSFile.CreateBMSFileFromFile(deletedChartPath);
            deletedFile.SetHash(source.hash);
            deletedFile.SetSha256(source.sha256);

            using (var songDb = new LR2SongDBExtended(songDbPath))
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

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [deletedFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                },
                0L,
                () => null,
                null);

            CollectionAssert.Contains(result.DeletedPaths, deletedChartPath);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + deletedFile.hash + "';"));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_KeepsSharedChartDigestRowsWhenAnotherSongStillUsesSameMd5()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string keepChartPath = Path.Combine(lr2RootPath, "Keep", "keep.bms");
            string deletedChartPath = Path.Combine(lr2RootPath, "Deleted", "deleted.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(keepChartPath));
            Directory.CreateDirectory(Path.GetDirectoryName(deletedChartPath));
            File.WriteAllText(keepChartPath, "#PLAYER 1\r\n#TITLE Same\r\n");
            File.Copy(keepChartPath, deletedChartPath, overwrite: true);

            var sourceKeep = BMSFile.CreateBMSFileFromFile(keepChartPath);
            var sourceDeleted = BMSFile.CreateBMSFileFromFile(deletedChartPath);
            var keepFile = new TestableBmsFile
            {
                path = keepChartPath
            };
            keepFile.SetHash(sourceKeep.hash);
            keepFile.SetSha256(sourceKeep.sha256);
            var deletedFile = new TestableBmsFile
            {
                path = deletedChartPath
            };
            deletedFile.SetHash(sourceDeleted.hash);
            deletedFile.SetSha256(sourceDeleted.sha256);

            using (var songDb = new LR2SongDBExtended(songDbPath))
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

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [keepFile, deletedFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [keepChartPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(keepChartPath), Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null);

            CollectionAssert.Contains(result.DeletedPaths, deletedChartPath);
            using var verify = new LR2SongDBExtended(songDbPath);
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

            var chartA = new TestableBmsFile
            {
                path = chartAPath
            };
            chartA.SetHash(BMSFile.CreateBMSFileFromFile(chartAPath).hash);
            var chartB = new TestableBmsFile
            {
                path = chartBPath
            };
            chartB.SetHash(BMSFile.CreateBMSFileFromFile(chartBPath).hash);
            chartB.SetSha256(new string('c', 64));

            var service = new BmsLibraryInitializationService();
            List<(int Total, int Processed, string Path)> progress = [];
            ChartDigestBackfillResult result = service.BackfillChartDigests(
                new BmsLibraryDbGateway(songDbPath),
                [chartA, chartB],
                (total, processed, path) => progress.Add((total, processed, path)));

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(0, result.FailedCount);
            Assert.IsFalse(string.IsNullOrWhiteSpace(chartA.sha256));
            Assert.AreEqual(new string('c', 64), chartB.sha256);
            Assert.IsTrue(progress.Any((item) => item.Total == 1 && item.Processed == 1));

            using var songDb = new LR2SongDBExtended(songDbPath);
            List<LR2SongDBExtended.chart_digest_map> rows = [.. songDb.Table<LR2SongDBExtended.chart_digest_map>()];
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
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(row, typeof(LR2SongDBExtended.bmson_song));
            }

            var service = new BmsLibraryInitializationService();
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
            string sha256 = new('1', 64);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                var song = new TestableBmsFile
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
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertChartInfos([CreateMinimalChartInfoRow(sha256, md5)]);

            var service = new BmsLibraryInitializationService();
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
            var deletedSong = new LR2SongDBExtended.bmson_song
            {
                path = deletedBmsonPath,
                folder = Path.GetDirectoryName(deletedBmsonPath),
                title = "Deleted",
                md5 = new string('a', 32),
                sha256 = new string('b', 64),
                updated_at = DateTime.UtcNow.AddDays(-1)
            };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(keepSong, typeof(LR2SongDBExtended.bmson_song));
                songDb.InsertOrReplace(deletedSong, typeof(LR2SongDBExtended.bmson_song));
            }

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [keepSong, deletedSong],
                executeBmsonScan: () => new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [keepBmsonPath, addedBmsonPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(keepBmsonPath), Array.Empty<string>() },
                            { Path.GetDirectoryName(addedBmsonPath), Array.Empty<string>() }
                        })
                });

            CollectionAssert.Contains(result.DeletedBmsonPaths, deletedBmsonPath);
            Assert.AreEqual(1, result.AddedBmsonSongs.Count);
            Assert.IsTrue(result.AddedBmsonSongs[0].HasFreshResourceReferences);
            Assert.AreEqual(2, result.NextBmsonSongs.Count);
            Assert.IsTrue(result.NextBmsonSongs.Any(song => string.Equals(song.path, keepBmsonPath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(result.NextBmsonSongs.Any(song => string.Equals(song.path, addedBmsonPath, StringComparison.OrdinalIgnoreCase)));

            using var verify = new LR2SongDBExtended(songDbPath);
            List<LR2SongDBExtended.bmson_song> rows = [.. verify.Table<LR2SongDBExtended.bmson_song>()];
            Assert.AreEqual(2, rows.Count);
            Assert.IsTrue(rows.Any(song => string.Equals(song.path, keepBmsonPath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(rows.Any(song => string.Equals(song.path, addedBmsonPath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(rows.Any(song => string.Equals(song.path, deletedBmsonPath, StringComparison.OrdinalIgnoreCase)));
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
            var oldTimestamp = new DateTime(2026, 5, 1, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsonPath, oldTimestamp);
            LR2SongDBExtended.bmson_song existingSong = BmsonSongParser.Parse(bmsonPath);

            File.WriteAllText(bmsonPath, CreateBmsonJson("New", "", "", "Artist", "Genre", 7, "beat-7k"));
            var newTimestamp = new DateTime(2026, 5, 2, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsonPath, newTimestamp);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(existingSong, typeof(LR2SongDBExtended.bmson_song));
            }

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsonPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(bmsonPath), Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [existingSong]);

            Assert.AreEqual(1, result.AddedBmsonSongs.Count);
            Assert.AreEqual("New", result.AddedBmsonSongs[0].title);
            Assert.AreEqual(newTimestamp, result.AddedBmsonSongs[0].updated_at);
            Assert.IsTrue(result.AddedBmsonSongs[0].HasFreshResourceReferences);
            Assert.AreEqual(1, result.NextBmsonSongs.Count);
            Assert.AreEqual("New", result.NextBmsonSongs[0].title);

            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDBExtended.bmson_song row = verify.Table<LR2SongDBExtended.bmson_song>().Single();
            Assert.AreEqual("New", row.title);
            Assert.AreEqual(newTimestamp, row.updated_at);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_UnchangedBmsonDoesNotParseOrMarkFresh()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsonPath = Path.Combine(lr2RootPath, "Keep", "keep.bmson");
            Directory.CreateDirectory(Path.GetDirectoryName(bmsonPath));
            File.WriteAllText(bmsonPath, CreateBmsonJson("Keep", "", "", "Artist", "Genre", 5, "beat-5k"));
            var timestamp = new DateTime(2026, 5, 3, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsonPath, timestamp);
            var currentSong = new LR2SongDBExtended.bmson_song
            {
                path = bmsonPath,
                folder = Path.GetDirectoryName(bmsonPath),
                title = "Keep",
                md5 = new string('a', 32),
                sha256 = new string('b', 64),
                updated_at = timestamp
            };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(currentSong, typeof(LR2SongDBExtended.bmson_song));
            }

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsonPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(bmsonPath), Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [currentSong]);

            Assert.AreEqual(0, result.BmsonUpsertTargetCount);
            Assert.AreEqual(0, result.AddedBmsonSongs.Count);
            Assert.AreEqual(1, result.NextBmsonSongs.Count);
            Assert.IsFalse(result.NextBmsonSongs[0].HasFreshResourceReferences);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_InvalidUpdatedBmsonKeepsExistingCatalogWithoutFreshRefs()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsonPath = Path.Combine(lr2RootPath, "Updated", "chart.bmson");
            Directory.CreateDirectory(Path.GetDirectoryName(bmsonPath));
            File.WriteAllText(bmsonPath, CreateBmsonJson("Old", "", "", "Artist", "Genre", 5, "beat-5k"));
            var oldTimestamp = new DateTime(2026, 5, 1, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsonPath, oldTimestamp);
            LR2SongDBExtended.bmson_song existingSong = BmsonSongParser.Parse(bmsonPath);
            existingSong.HasFreshResourceReferences = false;

            File.WriteAllText(bmsonPath, "{ \"info\": { \"title\": \"Broken\" }, \"bga\": \"unterminated", new UTF8Encoding(false));
            var newTimestamp = new DateTime(2026, 5, 2, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsonPath, newTimestamp);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(existingSong, typeof(LR2SongDBExtended.bmson_song));
            }

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsonPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(bmsonPath), Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [existingSong]);

            Assert.AreEqual(1, result.BmsonUpsertTargetCount);
            Assert.AreEqual(0, result.AddedBmsonSongs.Count);
            Assert.AreEqual(1, result.NextBmsonSongs.Count);
            Assert.AreEqual("Old", result.NextBmsonSongs[0].title);
            Assert.IsFalse(result.NextBmsonSongs[0].HasFreshResourceReferences);
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

            var keepFile = new TestableBmsFile
            {
                path = bmsPath
            };
            keepFile.SetHash(BMSFile.CreateBMSFileFromFile(bmsPath).hash);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(keepFile, typeof(LR2SongDB.song));
            }

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [keepFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
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
                currentBmsonSongs: [],
                executeBmsonScan: () => new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsonPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { bmsonDir, new[] { Path.Combine("sound", "song.ogg") } }
                        })
                });

            Assert.IsNotNull(result.NextDirectoryResourceLookupCache);
            CollectionAssert.Contains(result.NextDirectoryResourceLookupCache.Keys.ToList(), bmsDir);
            Assert.IsTrue(result.NextDirectoryResourceLookupCache.Keys.Contains(bmsonDir, StringComparer.OrdinalIgnoreCase));
            DirectoryResourceLookupCache.Entry bmsonEntry = result.NextDirectoryResourceLookupCache.GetEntryOrNull(bmsonDir);
            Assert.IsNotNull(bmsonEntry);
            Assert.AreEqual(1, bmsonEntry.AudioFileNameHashCount);
            Assert.AreEqual(1, bmsonEntry.AudioRelativePathHashArray.Length);
        });
    }

    [TestMethod]
    public void RunInitialize_InvokesAllPhasesAndWaitsForContinuations()
    {
        var service = new BmsLibraryInitializationService();
        int phase1Count = 0;
        int phase2Count = 0;
        int phase3Count = 0;
        int continuationCount = 0;

        InitializationExecutionResult result = service.RunInitialize(
            [
                delegate
                {
                    Interlocked.Increment(ref continuationCount);
                }
            ],
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
        Assert.IsTrue(result.WaitBeforeContinuationStartMs >= 0);
        Assert.IsTrue(result.WaitForContinuationSignalMs >= 0);
        Assert.IsTrue(result.WaitForContinuationTasksMs >= 0);
        Assert.AreEqual(
            result.WaitBeforeContinuationStartMs + result.WaitForContinuationSignalMs + result.WaitForContinuationTasksMs,
            result.WaitContinuationMs);
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

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(new ChartPackage
                {
                    path = directoryPackagePath,
                    delete_parent = false
                }, typeof(LR2SongDBExtended.install));
                songDb.InsertOrReplace(new ChartPackage
                {
                    path = singleFileChartPath,
                    delete_parent = false
                }, typeof(LR2SongDBExtended.install));
                songDb.InsertOrReplace(new ChartPackage
                {
                    path = Path.Combine(lr2RootPath, "MissingPkg"),
                    delete_parent = false
                }, typeof(LR2SongDBExtended.install));
            }

            string installedHash = BMSFile.CreateBMSFileFromFile(directoryChartPath).hash;
            var service = new BmsLibraryInitializationService();

            InstallTableLoadResult result = service.LoadInstallTable(
                new BmsLibraryDbGateway(songDbPath),
                chart => string.Equals(chart?.Md5, installedHash, StringComparison.OrdinalIgnoreCase));

            Assert.AreEqual(2, result.PendingPackages.Count);
            Assert.AreEqual(1, result.StaleInstallPaths.Count);
            Assert.AreEqual(1, result.InstalledWarningCount);
            Assert.AreEqual(1, result.SingleFileWarningCount);
            Assert.AreEqual(0, result.StrictWarningCount);
            Assert.IsTrue(result.LoadMs >= 0);
            Assert.IsTrue(result.WarningInitMs >= 0);
            Assert.IsTrue(result.TotalMs >= 0);
            ChartPackage installedWarningPackage = result.PendingPackages.Single(pkg => pkg.path.Equals(directoryPackagePath, StringComparison.OrdinalIgnoreCase));
            ChartPackage singleFileWarningPackage = result.PendingPackages.Single(pkg => pkg.path.Equals(singleFileChartPath, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(installedWarningPackage.GetBmsOwnersForTest()[0].Warnings.Contains(ChartWarningKind.AlreadyInstalled));
            Assert.IsTrue(singleFileWarningPackage.GetBmsOwnersForTest()[0].Warnings.Contains(ChartWarningKind.SingleBmsFile));
        });
    }

    [TestMethod]
    public void LoadInstallTable_ChecksInstalledChartsWithoutMaterializingUnmatchedBmsonEntries()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string directoryPackagePath = Path.Combine(lr2RootPath, "PendingBmsonDir");
            Directory.CreateDirectory(directoryPackagePath);
            string installedBmsonPath = CreateBmsonFile(directoryPackagePath, "installed.bmson", "Installed", "Artist");
            string unmatchedBmsonPath = Path.Combine(directoryPackagePath, "unmatched.bmson");
            File.WriteAllText(unmatchedBmsonPath, "{\"version\":\"1.0.0\",\"info\":{\"title\":\"Unmatched\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\"},\"sound_channels\":[],\"lines\":[{\"y\":0}]}");

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(new ChartPackage
                {
                    path = directoryPackagePath,
                    delete_parent = false
                }, typeof(LR2SongDBExtended.install));
            }

            var service = new BmsLibraryInitializationService();

            InstallTableLoadResult result = service.LoadInstallTable(
                new BmsLibraryDbGateway(songDbPath),
                chart => string.Equals(chart?.Path, installedBmsonPath, StringComparison.OrdinalIgnoreCase));

            ChartPackage pendingPackage = result.PendingPackages.Single();
            PackageChartEntry installedEntry = pendingPackage.ChartEntries.Single(entry => string.Equals(entry.Chart.Path, installedBmsonPath, StringComparison.OrdinalIgnoreCase));
            PackageChartEntry unmatchedEntry = pendingPackage.ChartEntries.Single(entry => string.Equals(entry.Chart.Path, unmatchedBmsonPath, StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(1, result.InstalledWarningCount);
            Assert.IsNull(installedEntry.GetBmsOwnerForTest());
            Assert.IsTrue(installedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
            Assert.IsNull(unmatchedEntry.GetBmsOwnerForTest());
        });
    }

    [TestMethod]
    public void LoadInstallTable_AddsBmsonResourceWarningWithoutMaterializingCompatibilityAdapter()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string directoryPackagePath = Path.Combine(lr2RootPath, "PendingBmsonResource");
            Directory.CreateDirectory(directoryPackagePath);
            string bmsonPath = Path.Combine(directoryPackagePath, "chart.bmson");
            File.WriteAllText(bmsonPath, CreateBmsonJsonWithSound("missing.wav"));

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(new ChartPackage
                {
                    path = directoryPackagePath,
                    delete_parent = false
                }, typeof(LR2SongDBExtended.install));
            }

            var service = new BmsLibraryInitializationService();

            InstallTableLoadResult result = service.LoadInstallTable(
                new BmsLibraryDbGateway(songDbPath),
                _ => false);

            ChartPackage pendingPackage = result.PendingPackages.Single();
            PackageChartEntry entry = pendingPackage.ChartEntries.Single();
            Assert.AreEqual(1, result.StrictWarningCount);
            Assert.IsNull(entry.GetBmsOwnerForTest());
            Assert.AreEqual(ChartFileKind.Bmson, entry.Chart.Kind);
            Assert.IsTrue(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
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

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(new ChartPackage
                {
                    path = directoryPackagePath,
                    delete_parent = false
                }, typeof(LR2SongDBExtended.install));
            }

            var service = new BmsLibraryInitializationService();
            InstallTableLoadResult result = service.LoadInstallTable(
                new BmsLibraryDbGateway(songDbPath),
                file => false);

            Assert.AreEqual(1, result.PendingPackages.Count);
            Assert.AreEqual(2, result.PendingPackages[0].GetBmsOwnersForTest().Count);
            BMSFile nestedChart = result.PendingPackages[0].GetBmsOwnersForTest().Single(file => Path.GetFileName(file.path).Equals("another.bms", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(nestedChart.Warnings.Contains(ChartWarningKind.NestedChartFileInPackage));
            Assert.IsTrue(nestedChart.Warnings.Contains(ChartWarningKind.ResourceWavMissing));
            Assert.AreEqual("[2] " + BeMusicSeeker.Properties.Resources.WarningDigest_NestedChart + ", " + BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing, nestedChart.Warnings.BuildDigestText());
            StringAssert.Contains(nestedChart.Warnings.BuildTooltipText(), BeMusicSeeker.Properties.Resources.Warning_NestedChartFileInPackage);
            StringAssert.Contains(nestedChart.Warnings.BuildTooltipText(), "WAV");
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

            using (var songDb = new LR2SongDBExtended(songDbPath))
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

            var dialogService = new RecordingDialogService
            {
                ResultToReturn = MessageBoxResult.Yes
            };
            var fileMutationService = new RecordingFileMutationService();
            var service = new BmsLibraryInitializationService();

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
            Assert.IsTrue(result.UpdatedFolders.Any(folder => string.Equals(folder.path, folderPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(dialogService.Calls.Any(call => call.Button == MessageBoxButton.YesNo));
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

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(new ChartPackage
                {
                    path = singleFileChartPath,
                    delete_parent = false
                }, typeof(LR2SongDBExtended.install));
            }

            var service = new BmsLibraryInitializationService();
            InstallTableLoadResult result = service.LoadInstallTable(
                new BmsLibraryDbGateway(songDbPath),
                file => false);

            Assert.AreEqual(1, result.PendingPackages.Count);
            Assert.AreEqual(1, result.SingleFileWarningCount);
            Assert.IsTrue(result.PendingPackages[0].ChartEntries[0].Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.SingleBmsonFile));
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

            using (var songDb = new LR2SongDBExtended(songDbPath))
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

            var dialogService = new RecordingDialogService
            {
                ResultToReturn = MessageBoxResult.Yes
            };
            var fileMutationService = new RecordingFileMutationService();
            var service = new BmsLibraryInitializationService();

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
            Assert.IsFalse(dialogService.Calls.Any(call => call.Button == MessageBoxButton.YesNo));
        });
    }

    private static void WithTemporaryLr2SongDb(Action<string, string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_InitTests_" + Guid.NewGuid().ToString("N"));
        string lr2FilesPath = Path.Combine(tempRootPath, "LR2files");
        string databaseDirectoryPath = Path.Combine(lr2FilesPath, "Database");
        string songDbPath = Path.Combine(databaseDirectoryPath, "song.db");
        Directory.CreateDirectory(databaseDirectoryPath);
        File.WriteAllBytes(songDbPath, []);
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

    private static void WithTemporaryStandaloneSongDb(Action<string, string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_StandaloneInitTests_" + Guid.NewGuid().ToString("N"));
        string databaseDirectoryPath = Path.Combine(tempRootPath, "data");
        string songDbPath = Path.Combine(databaseDirectoryPath, "song.db");
        Directory.CreateDirectory(databaseDirectoryPath);
        File.WriteAllBytes(songDbPath, []);
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

    private static string CreateBmsonJsonWithSound(string soundFileName)
    {
        return "{"
            + "\"version\":\"1.0.0\","
            + "\"info\":{\"title\":\"Resource\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\"},"
            + "\"sound_channels\":[{\"name\":\"" + soundFileName + "\",\"notes\":[]}],"
            + "\"lines\":[{\"y\":0}]"
            + "}";
    }

    private static string CreateBmsonFile(string directoryPath, string fileName, string title, string artist)
    {
        Directory.CreateDirectory(directoryPath);
        string filePath = Path.Combine(directoryPath, fileName);
        File.WriteAllText(filePath, CreateBmsonJson(title, string.Empty, string.Empty, artist, string.Empty, 1, "beat-7k"));
        return filePath;
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
            [],
            new ChartScanExecutionResult
            {
                Success = true,
                Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
            },
            0L,
            () => null,
            null,
            currentBmsonSongs: []);
    }

    private static SongTableFileCheckResult RunWithCommitChunkSizeOverride(int? overrideValue, string songDbPath)
    {
        BmsLibraryInitializationService service = overrideValue.HasValue
            ? new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1, fileDiffCommitChunkSizeOverride: overrideValue.Value)
            : new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
        return service.ApplyFileScanDiff(
            new BmsLibraryDbGateway(songDbPath),
            new BmsLibraryOptionsSnapshot(),
            [],
            new ChartScanExecutionResult
            {
                Success = true,
                Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
            },
            0L,
            () => null,
            null,
            currentBmsonSongs: []);
    }

    private static SongTableFileCheckResult RunWithInlineChartInfoBatchSizeOverride(int? overrideValue, string songDbPath)
    {
        BmsLibraryInitializationService service = overrideValue.HasValue
            ? new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1, inlineChartInfoBatchSizeOverride: overrideValue.Value)
            : new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
        return service.ApplyFileScanDiff(
            new BmsLibraryDbGateway(songDbPath),
            new BmsLibraryOptionsSnapshot(),
            [],
            new ChartScanExecutionResult
            {
                Success = true,
                Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
            },
            0L,
            () => null,
            null,
            currentBmsonSongs: []);
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

    private static ChartScanResult CreateScanResult(IEnumerable<string> chartPaths, IDictionary<string, IEnumerable<string>> resourcesByDirectory)
    {
        var chartPathSet = new HashSet<string>(chartPaths ?? [], StringComparer.OrdinalIgnoreCase);
        var chartDirectories = new HashSet<string>(
            chartPathSet.Select(Path.GetDirectoryName).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        foreach (string directoryPath in resourcesByDirectory?.Keys ?? Enumerable.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                chartDirectories.Add(directoryPath);
            }
        }

        var result = new ChartScanResult
        {
            ChartFilePaths = chartPathSet,
            ChartDirectories = chartDirectories
        };
        var cache = new DirectoryResourceLookupCache();
        foreach (string chartDirectory in chartDirectories)
        {
            IEnumerable<string>? resourceFiles = null;
            resourcesByDirectory?.TryGetValue(chartDirectory, out resourceFiles);
            cache.AddDir(chartDirectory, resourceFiles ?? []);
            DirectoryResourceLookupCache.Entry entry = cache.GetEntryOrNull(chartDirectory) ?? new DirectoryResourceLookupCache.Entry();
            result.AudioRelativePathHashesByChartDirectory[chartDirectory] = entry.AudioRelativePathHashArray;
            result.ImageRelativePathHashesByChartDirectory[chartDirectory] = entry.ImageRelativePathHashArray;
            result.MovieRelativePathHashesByChartDirectory[chartDirectory] = entry.MovieRelativePathHashArray;
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
        public List<TimestampCall> TimestampCalls { get; } = [];

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
        public List<DialogCall> Calls { get; } = [];

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
