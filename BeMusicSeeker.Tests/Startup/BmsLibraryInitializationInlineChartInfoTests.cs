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
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.BmsLibraryInitializationTestSupport;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;
namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class BmsLibraryInitializationInlineChartInfoTests
{
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
                new EverythingNative(ApplicationPathPolicy.Current),
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
            Assert.AreEqual(0, result.InlineChartInfoRows.Count);
            Assert.AreEqual(0, result.InlineChartInfoAppliedRows.Count);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ? AND md5 = ?;", result.AddedFiles[0].sha256, result.AddedFiles[0].hash));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_parse_failure;"));
            LR2SongDBExtended.chart_info chartInfoRow = verify.Query<LR2SongDBExtended.chart_info>("SELECT * FROM chart_info WHERE sha256 = ? AND md5 = ?;", result.AddedFiles[0].sha256, result.AddedFiles[0].hash).Single();
            Assert.AreEqual(BmsLibraryDbGateway.CurrentChartInfoParserVersion, chartInfoRow.parser_version);
            LR2SongDB.song songRow = verify.Table<LR2SongDB.song>().Single();
            Assert.AreEqual(120, songRow.maxbpm);
            Assert.AreEqual(120, songRow.minbpm);
            Assert.AreEqual(5, songRow.mode);
            Assert.AreEqual(1, songRow.karinotes);
            Assert.AreEqual(0, songRow.longnote);
            Assert.AreEqual(0, songRow.random);
            Assert.AreEqual(2, songRow.judge);
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
                new EverythingNative(ApplicationPathPolicy.Current),
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
            Assert.IsTrue(result.InlineBmsMaintenanceWallMs >= 0);
            Assert.IsNull(added.WAVfiles);
            Assert.IsNull(added.BGAfiles);
            Assert.AreEqual(1, added.maintenanceInfo.wav_files_defined);
            Assert.AreEqual(1, added.maintenanceInfo.wav_files_existing);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(result.InlineMaintenanceSuccessCount, verify.ExecuteScalar<int>("SELECT COUNT(1) FROM maintenance;"));
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
                new EverythingNative(ApplicationPathPolicy.Current),
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
                new EverythingNative(ApplicationPathPolicy.Current),
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
                new EverythingNative(ApplicationPathPolicy.Current),
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
                new EverythingNative(ApplicationPathPolicy.Current),
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
                new EverythingNative(ApplicationPathPolicy.Current),
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
                new EverythingNative(ApplicationPathPolicy.Current),
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
    public void EnsureSongLookupIndexes_CreatesLr2CompatibleLookupIndexes()
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
            Assert.AreEqual(
                1L,
                verify.ExecuteScalar<long>(
                    "SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = ?;",
                    BmsLibraryDbGateway.SongPathNocaseIndexName));
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
                new EverythingNative(ApplicationPathPolicy.Current),
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
            Assert.AreEqual(result.AddedFiles[0].hash, callbackRows[0].md5);
            Assert.AreEqual(BmsLibraryDbGateway.CurrentChartInfoParserVersion, callbackRows[0].parser_version);

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
                new EverythingNative(ApplicationPathPolicy.Current),
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
                new EverythingNative(ApplicationPathPolicy.Current),
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
            Assert.AreEqual(0, result.InlineChartInfoAppliedRows.Count);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song;"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_parse_failure WHERE md5 = ?;", snapshot.Md5));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_CurrentInlineChartInfoRowSkipsParseAndReturnsAppliedRow()
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
                new EverythingNative(ApplicationPathPolicy.Current),
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
            Assert.AreEqual(0, result.InlineChartInfoAppliedRows.Count);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ? AND md5 = ?;", result.AddedFiles[0].sha256, result.AddedFiles[0].hash));
            LR2SongDB.song songRow = verify.Table<LR2SongDB.song>().Single();
            Assert.AreEqual(7, songRow.level);
            Assert.AreEqual(4, songRow.difficulty);
            Assert.AreEqual(1, songRow.karinotes);
            Assert.AreEqual(5, songRow.mode);
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
                new EverythingNative(ApplicationPathPolicy.Current),
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
            Assert.AreEqual(0, result.InlineChartInfoRows.Count);
            Assert.AreEqual(0, result.InlineChartInfoAppliedRows.Count);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_FileDiffParserDegree_UsesDefaultAndNormalizesOverrides()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            int expectedDefault = FileScanParseCommitOwner.ResolveDefaultFileDiffParserDegree(Environment.ProcessorCount);
            Assert.AreEqual(expectedDefault, FileScanParseCommitOwner.ResolveDefaultFileDiffParserDegree());
            Assert.AreEqual(1, FileScanParseCommitOwner.ResolveDefaultFileDiffParserDegree(1));
            Assert.AreEqual(1, FileScanParseCommitOwner.ResolveDefaultFileDiffParserDegree(2));
            Assert.AreEqual(2, FileScanParseCommitOwner.ResolveDefaultFileDiffParserDegree(4));
            Assert.AreEqual(4, FileScanParseCommitOwner.ResolveDefaultFileDiffParserDegree(8));
            Assert.AreEqual(8, FileScanParseCommitOwner.ResolveDefaultFileDiffParserDegree(16));
            Assert.AreEqual(1, FileScanParseCommitOwner.ResolveDefaultFileDiffPostParseWorkerDegree(1, 1));
            Assert.AreEqual(3, FileScanParseCommitOwner.ResolveDefaultFileDiffPostParseWorkerDegree(4, 2));
            Assert.AreEqual(6, FileScanParseCommitOwner.ResolveDefaultFileDiffPostParseWorkerDegree(8, 4));
            Assert.AreEqual(12, FileScanParseCommitOwner.ResolveDefaultFileDiffPostParseWorkerDegree(16, 8));
            Assert.AreEqual(12, FileScanParseCommitOwner.ResolveDefaultFileDiffPostParseWorkerDegree(8, 12));

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
            Assert.AreEqual(10000, FileScanParseCommitOwner.ResolveDefaultFileDiffCommitChunkSize());

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
            Assert.AreEqual(2048, FileScanParseCommitOwner.ResolveDefaultInlineChartInfoBatchSize());

            Assert.AreEqual(2048, RunWithInlineChartInfoBatchSizeOverride(null, songDbPath).InlineChartInfoBatchSize);
            Assert.AreEqual(1, RunWithInlineChartInfoBatchSizeOverride(0, songDbPath).InlineChartInfoBatchSize);
            Assert.AreEqual(1, RunWithInlineChartInfoBatchSizeOverride(-10, songDbPath).InlineChartInfoBatchSize);
            Assert.AreEqual(2, RunWithInlineChartInfoBatchSizeOverride(2, songDbPath).InlineChartInfoBatchSize);
            Assert.AreEqual(512, RunWithInlineChartInfoBatchSizeOverride(512, songDbPath).InlineChartInfoBatchSize);
        });
    }

}
