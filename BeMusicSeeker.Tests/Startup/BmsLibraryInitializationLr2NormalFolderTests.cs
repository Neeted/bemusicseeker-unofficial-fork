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
                lr2NormalFolderSyncRootDirectories: [lr2RootPath],
                folderTableSnapshot: CaptureNormalFolderSnapshot(songDbPath));

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
                lr2NormalFolderSyncRootDirectories: [lr2RootPath],
                folderTableSnapshot: CaptureNormalFolderSnapshot(songDbPath));

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
    /// directory日時が同じでも全必要集合を生成し、folderinfoの新しいtitleを反映する。
    /// </summary>
    [TestMethod]
    public void ApplyFileScanDiff_UpdatesFolderInfoWithUnchangedDirectoryMtimeWithoutSongDbDiff()
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
                lr2NormalFolderSyncRootDirectories: [lr2RootPath],
                folderTableSnapshot: CaptureNormalFolderSnapshot(songDbPath));

            Assert.IsFalse(result.HasDbDiff);
            Assert.IsTrue(result.Lr2NormalFolderSyncExecuted);
            Assert.IsFalse(result.Lr2NormalFolderSyncFailed);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder pack = verify.Table<LR2SongDB.folder>().Single(row => row.path == ToFolderPath(packDirectoryPath));
            Assert.AreEqual("Updated Folder", pack.title);
        });
    }

    /// <summary>
    /// 全登録file diff用に全folderの全列・全rawキーを一括取得し、customや相対キーも縮約せず保持する。
    /// </summary>
    [TestMethod]
    public void LoadFolderTableSnapshot_ReadsAllRawRowsAndColumnsForWholeOperation()
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
                        date = 3,
                        command = "Keep command",
                        max = 7,
                        adddate = 89,
                        subtitle = "Sub",
                        category = "Cat",
                        info_a = "A",
                        info_b = "B",
                        banner = "banner.png",
                        parent = "Parent"
                    }, typeof(LR2SongDB.folder));
                    songDbConnection.InsertOrReplace(new LR2SongDB.folder
                    {
                        path = ToFolderPath(outsideFolderPath),
                        title = "Outside",
                        type = 1,
                        date = 4
                    }, typeof(LR2SongDB.folder));
                    songDbConnection.Insert(new LR2SongDB.folder { path = "Pack\\", type = 1, title = "Relative", adddate = 91 });
                    songDbConnection.Insert(new LR2SongDB.folder { path = ToFolderPath(packDirectoryPath).ToUpperInvariant(), type = 1, title = "Alias", adddate = 92 });
                });
            }

            var logs = new List<string>();
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            Lr2FolderTableSnapshot snapshot = service.LoadFolderTableSnapshot(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                });

            Assert.IsNotNull(snapshot);
            Assert.IsTrue(snapshot.ExistingRowsByPath.ContainsKey(ToFolderPath(lr2RootPath)));
            Assert.IsTrue(snapshot.ExistingRowsByPath.ContainsKey(ToFolderPath(packDirectoryPath)));
            Assert.IsTrue(snapshot.ExistingRowsByPath.ContainsKey(appManagedLr2FolderPath));
            Assert.IsTrue(snapshot.ExistingRowsByPath.ContainsKey(ToFolderPath(outsideFolderPath)));
            Assert.AreEqual(6, snapshot.ExistingRowCount);
            Assert.AreEqual("Pack", snapshot.ExistingRowsByPath[ToFolderPath(packDirectoryPath)].title);
            Assert.AreEqual("Alias", snapshot.ExistingRowsByPath[ToFolderPath(packDirectoryPath).ToUpperInvariant()].title);
            Assert.AreEqual(91, snapshot.ExistingRowsByPath["Pack\\"].adddate);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            foreach (LR2SongDB.folder row in verify.Table<LR2SongDB.folder>())
            {
                LR2SongDB.folder captured = snapshot.ExistingRowsByPath[row.path];
                Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(row), System.Text.Json.JsonSerializer.Serialize(captured));
            }
        });
    }

    /// <summary>
    /// Fullの補正後全件結果またはReloadの全行一括結果を再利用し、ASCII/CP932 Greekの旧raw keyとexact存続行を比較へ渡す。
    /// mtime同一でも旧exactを退役し、削除譜面とcustom-onlyの必要親をcurrent scanから確定して、親CRC・利用者列・score・残存譜面を保ち二周期で収束する。
    /// </summary>
    [DataTestMethod]
    [DataRow(false, false, false)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, true, false)]
    [DataRow(true, false, true)]
    [DataRow(true, true, true)]
    public void ApplyFileScanDiff_ReusesOperationFolderSnapshot(bool nonAsciiCase, bool fullLoad, bool deleteChart)
    {
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string root = nonAsciiCase ? Path.Combine(lr2RootPath, "α") : lr2RootPath;
            string oldRoot = nonAsciiCase ? Path.Combine(lr2RootPath, "Α") : root.ToLowerInvariant();
            var cp932 = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            Assert.AreEqual(root, cp932.GetString(cp932.GetBytes(root)));
            Assert.AreEqual(oldRoot, cp932.GetString(cp932.GetBytes(oldRoot)));
            Assert.IsFalse(string.Equals(root, oldRoot, StringComparison.Ordinal));
            Assert.IsTrue(string.Equals(root, oldRoot, StringComparison.OrdinalIgnoreCase));
            string pack = Directory.CreateDirectory(Path.Combine(root, "Pack")).FullName;
            string oldPack = nonAsciiCase ? Path.Combine(oldRoot, "Pack") : pack.ToLowerInvariant();
            string bmsPath = Path.Combine(pack, "current.bms");
            string folderInfoPath = Path.Combine(pack, "folderinfo.txt");
            string contents = CreateValidBmsText("Current");
            File.WriteAllText(bmsPath, contents, Encoding.ASCII);
            File.WriteAllText(folderInfoPath, "#TITLE Updated Folder", cp932);
            string customDirectory = Path.Combine(root, "CustomOnly");
            string oldCustomDirectory = Path.Combine(oldRoot, "CustomOnly");
            string customPath = Path.Combine(customDirectory, "List.lr2folder");
            var oldCustomRow = new LR2SongDB.folder
            {
                path = Path.Combine(oldCustomDirectory, "List.lr2folder"),
                type = 2,
                title = "Keep custom",
                adddate = 654,
                date = 9,
                command = "Keep command",
                parent = "Keep parent"
            };
            if (deleteChart)
            {
                Directory.CreateDirectory(customDirectory);
                File.WriteAllText(customPath, "#TITLE Current custom", cp932);
            }
            DateTime timestamp = new(2026, 6, 8, 1, 0, 0, DateTimeKind.Utc);
            DateTime generatedAt = timestamp.AddDays(10);
            File.SetLastWriteTimeUtc(bmsPath, timestamp);
            File.SetLastWriteTimeUtc(folderInfoPath, timestamp.AddMinutes(5));
            Directory.SetLastWriteTimeUtc(pack, timestamp);
            Directory.SetLastWriteTimeUtc(root, timestamp);
            if (deleteChart) { Directory.SetLastWriteTimeUtc(customDirectory, timestamp); }
            // ParseSnapshotはDateを設定しないため、既に読込み済みのsong入力として保存日時も捕捉する。
            ChartFile currentFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsPath)) with
            { Date = ToUnixSeconds(timestamp), AddDate = 4321, Favorite = 3, Tag = "keep-user", Txt = 0 };
            string removedPath = Path.Combine(pack, "removed.bms");
            ChartFile? removedFile = null;
            if (deleteChart)
            {
                File.WriteAllText(removedPath, CreateValidBmsText("Removed"), Encoding.ASCII);
                File.SetLastWriteTimeUtc(removedPath, timestamp);
                removedFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(removedPath)) with
                { Date = ToUnixSeconds(timestamp), AddDate = 5432, Txt = 0 };
                Assert.AreNotEqual(currentFile.Md5, removedFile.Md5);
            }
            string[] outside = [ToFolderPath(oldRoot + "Other"), ToFolderPath(Path.Combine(Path.GetDirectoryName(root)!, "Unrelated"))];
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.RunInTransaction(() =>
                {
                    db.CreateTable<LR2SongDB.song>(); db.CreateTable<LR2SongDB.folder>();
                    db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(currentFile), typeof(LR2SongDB.song));
                    if (removedFile != null) { db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(removedFile), typeof(LR2SongDB.song)); }
                    db.InsertOrReplace(new LR2SongDB.folder { path = ToFolderPath(oldRoot), adddate = 123, title = Path.GetFileName(root), type = 1, date = ToUnixSeconds(timestamp) }, typeof(LR2SongDB.folder));
                    db.InsertOrReplace(new LR2SongDB.folder { path = ToFolderPath(oldPack), adddate = 234, title = "Old Folder", type = 1, date = ToUnixSeconds(timestamp) }, typeof(LR2SongDB.folder));
                    if (deleteChart)
                    {
                        db.Insert(oldCustomRow);
                        db.Insert(new LR2SongDB.folder { path = ToFolderPath(oldCustomDirectory), adddate = 456, title = "Old custom parent", type = 1, date = ToUnixSeconds(timestamp) });
                        db.Insert(new LR2SongDB.folder { path = ToFolderPath(root), adddate = 321, title = "Current Root", type = 1, date = ToUnixSeconds(timestamp), subtitle = "Root subtitle" });
                        db.Insert(new LR2SongDB.folder { path = ToFolderPath(pack), adddate = null, title = "Current Pack", type = 1, date = ToUnixSeconds(timestamp), command = "Current command" });
                    }
                    foreach (string path in outside)
                    { db.InsertOrReplace(new LR2SongDB.folder { path = path, adddate = 998, title = "keep-outside", parent = "keep-parent", type = 1, date = 9 }, typeof(LR2SongDB.folder)); }
                    db.Execute("CREATE TABLE retained_score(hash TEXT PRIMARY KEY, score INTEGER);");
                    db.Execute("INSERT INTO retained_score VALUES (?, ?);", currentFile.Md5, 9876);
                });
            }
            if (deleteChart) { File.Delete(removedPath); }
            // 譜面削除後もmtimeを同値へ戻し、実Applyが生産する削除親scopeと旧raw exactの接続を検証する。
            Directory.SetLastWriteTimeUtc(pack, timestamp);
            Directory.SetLastWriteTimeUtc(root, timestamp);
            var config = new Lr2ConfigOwner(LR2Config.CreateRootPlanningDocument(Path.Combine(lr2RootPath, "config.xml"), [root]));
            BmsLibraryOptionsSnapshot options = new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true, LR2RootPath = lr2RootPath }.WithLr2Input(config.Capture());
            ChartScanResult scan = CreateScanResult([bmsPath], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase) { [pack] = [] });
            RootFileEnumerationEntry folderInfoEntry = new(folderInfoPath, File.GetLastWriteTimeUtc(folderInfoPath), new FileInfo(folderInfoPath).Length);
            if (deleteChart)
            {
                scan.DirectoryEntriesByPath[customDirectory] = new RootFileEnumerationEntry(customDirectory, timestamp);
            }
            scan.FolderInfoFilePaths.Add(folderInfoPath); scan.FolderInfoFileEntriesByPath[folderInfoPath] = folderInfoEntry;
            string[] scanPaths = scan.ChartFilePaths.ToArray();
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            // Greekでは登録・song・実file・scan・prefetch要求の全てが現αで、folderだけが旧Α。
            Lr2FolderTableSnapshot Capture() => fullLoad
                ? service.LoadSongTable(gateway, options, null, new TestFileMutationService(), null, failure => failure.Message).FolderTableSnapshot
                : service.LoadFolderTableSnapshot(gateway, options);
            Lr2FolderTableSnapshot snapshot = Capture();
            Assert.AreEqual(deleteChart ? 8 : 4, snapshot.ExistingRowCount);
            string[] rawSnapshot = snapshot.ExistingRowsByPath.Values.Select(row => row.path).ToArray();
            CollectionAssert.AreEquivalent(new[] { ToFolderPath(oldRoot), ToFolderPath(oldPack) }.Concat(outside).Concat(deleteChart ? new[] { ToFolderPath(root), ToFolderPath(pack), ToFolderPath(oldCustomDirectory), oldCustomRow.path } : []).ToArray(), rawSnapshot);
            if (deleteChart)
            {
                Assert.AreEqual("Root subtitle", snapshot.ExistingRowsByPath[ToFolderPath(root)].subtitle);
                Assert.AreEqual("Current command", snapshot.ExistingRowsByPath[ToFolderPath(pack)].command);
                // 実取得が全raw行を保持する観測と、列挙順に依存しない生成・planを分けます。
                foreach (LR2SongDB.folder[] rows in new[] { snapshot.Rows.ToArray(), snapshot.Rows.Reverse().ToArray() })
                {
                    Lr2FolderGenerationResult generated = Lr2FolderRowGenerator.GenerateNormalDirectoryRows(new()
                    {
                        RootDirectories = [root],
                        DirectoryPaths = [root, pack, customDirectory],
                        ExistingRows = rows,
                        DirectoryMetadataResolver = _ => new(timestamp),
                        GeneratedAtUtc = generatedAt
                    });
                    Lr2FolderGenerationSyncPlan plan = Lr2FolderGenerationScopePlanner.PlanNormalDirectorySync(generated, rows, [root]);
                    CollectionAssert.AreEquivalent(new[] { ToFolderPath(oldRoot), ToFolderPath(oldPack), ToFolderPath(oldCustomDirectory) }, plan.DeletePaths.ToArray());
                    Assert.AreEqual(321, generated.Rows.Single(row => row.path == ToFolderPath(root)).adddate);
                    Assert.IsNull(generated.Rows.Single(row => row.path == ToFolderPath(pack)).adddate);
                }
            }
            var logs = new List<string>();
            SongTableFileCheckResult Apply(Lr2FolderTableSnapshot prefetched, IEnumerable<ChartFile> current) => service.ApplyFileScanDiff(gateway,
                new EverythingNative(ApplicationPathPolicy.Current), options, current,
                new ChartScanExecutionResult { Success = true, Result = scan }, 0L, () => null, null,
                logInstallPerformance: logs.Add, currentBmsonSongs: [], lr2NormalFolderSyncRootDirectories: [root], folderTableSnapshot: prefetched, generatedAtUtc: generatedAt,
                lr2FolderDiscoveryRootDirectories: deleteChart ? [root] : [], rootFileEnumerator: new FastRootFileEnumerator());
            string VerifyDatabase()
            {
                using LR2SongDBExtended db = gateway.OpenSongDbReadOnly();
                LR2SongDB.folder rootRow = db.Query<LR2SongDB.folder>("SELECT * FROM folder WHERE path = ? COLLATE BINARY;", ToFolderPath(root)).Single();
                LR2SongDB.folder packRow = db.Query<LR2SongDB.folder>("SELECT * FROM folder WHERE path = ? COLLATE BINARY;", ToFolderPath(pack)).Single();
                Assert.AreEqual(deleteChart ? 321 : ToUnixSeconds(generatedAt), rootRow.adddate);
                Assert.AreEqual(deleteChart ? (int?)null : ToUnixSeconds(generatedAt), packRow.adddate);
                Assert.AreEqual("e2977170", rootRow.parent);
                // CRC計算自体の独立値は既存Goldenが担い、ここでは動的temp rootとの接続を確認する。
                Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(root), packRow.parent);
                Assert.AreEqual("Updated Folder", packRow.title);
                Assert.AreEqual(0, db.ExecuteScalar<int>("SELECT COUNT(*) FROM folder WHERE path = ? COLLATE BINARY OR path = ? COLLATE BINARY;", ToFolderPath(oldRoot), ToFolderPath(oldPack)));
                if (deleteChart)
                {
                    LR2SongDB.folder currentCustomParent = db.Table<LR2SongDB.folder>().Single(row => row.path == ToFolderPath(customDirectory));
                    Assert.AreEqual(ToUnixSeconds(generatedAt), currentCustomParent.adddate);
                    Assert.AreEqual(0, db.ExecuteScalar<int>("SELECT COUNT(*) FROM folder WHERE path = ? COLLATE BINARY;", ToFolderPath(oldCustomDirectory)));
                    LR2SongDB.folder keptCustom = db.Table<LR2SongDB.folder>().Single(row => row.path == oldCustomRow.path);
                    Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(oldCustomRow), System.Text.Json.JsonSerializer.Serialize(keptCustom),
                        "normal同期はcustom本体のraw行を保持し、その物理残存親だけをcurrent scan表記へ収束させます。");
                }
                foreach (string path in outside)
                {
                    LR2SongDB.folder row = db.Query<LR2SongDB.folder>("SELECT * FROM folder WHERE path = ? COLLATE BINARY;", path).Single();
                    Assert.AreEqual(998, row.adddate); Assert.AreEqual("keep-outside", row.title); Assert.AreEqual("keep-parent", row.parent); Assert.AreEqual(9, row.date);
                }
                LR2SongDB.song song = db.Table<LR2SongDB.song>().Single();
                Assert.AreEqual(bmsPath, song.path); Assert.AreEqual(4321, song.adddate); Assert.AreEqual(3, song.favorite); Assert.AreEqual("keep-user", song.tag);
                Assert.AreEqual(currentFile.Md5, song.hash); Assert.AreEqual(ToUnixSeconds(timestamp), song.date);
                Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(pack), song.folder);
                Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(root), song.parent);
                Assert.AreEqual(9876, db.ExecuteScalar<int>("SELECT score FROM song JOIN retained_score ON song.hash = retained_score.hash;"));
                Assert.AreEqual(contents, File.ReadAllText(bmsPath)); Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(bmsPath));
                Assert.AreEqual(timestamp, Directory.GetLastWriteTimeUtc(pack)); Assert.AreEqual(timestamp, Directory.GetLastWriteTimeUtc(root));
                Assert.AreEqual(0, db.ExecuteScalar<int>("SELECT COUNT(*) FROM song WHERE path = ? COLLATE BINARY;", removedPath));
                Assert.IsFalse(File.Exists(removedPath));
                return System.Text.Json.JsonSerializer.Serialize(new { Folders = db.Table<LR2SongDB.folder>().OrderBy(row => row.path).ToArray(), Song = song });
            }
            SongTableFileCheckResult first = Apply(snapshot, removedFile == null ? [currentFile] : [currentFile, removedFile]);
            Assert.IsTrue(first.Lr2NormalFolderSyncExecuted); Assert.IsFalse(first.Lr2NormalFolderSyncFailed);
            string firstState = VerifyDatabase();
            Assert.AreEqual(deleteChart, first.HasDbDiff, $"song差分は今回消失した譜面だけ: added={first.AddedFiles.Count}, deleted={first.DeletedPaths.Count}, dateOnly={first.BmsDateOnlyUpdateCount}, textOnly={first.BmsTextOnlyUpdateCount}");
            CollectionAssert.AreEqual(deleteChart ? new[] { removedPath } : [], first.DeletedPaths.ToArray());
            Assert.AreEqual(0, first.AddedFiles.Count + first.BmsDateOnlyUpdateCount + first.BmsTextOnlyUpdateCount);
            CollectionAssert.AreEqual(rawSnapshot, snapshot.ExistingRowsByPath.Values.Select(row => row.path).ToArray());
            Lr2FolderTableSnapshot secondSnapshot = Capture();
            SongTableFileCheckResult second = Apply(secondSnapshot, [currentFile]);
            Assert.AreEqual(firstState, VerifyDatabase(), "第二周期でも実folder/songの全保存値とscore・実譜面を保持します。");
            Assert.IsFalse(second.HasDbDiff, $"第二周期のsong差分は不要: added={second.AddedFiles.Count}, deleted={second.DeletedPaths.Count}, dateOnly={second.BmsDateOnlyUpdateCount}, textOnly={second.BmsTextOnlyUpdateCount}");
            Assert.IsTrue(second.Lr2NormalFolderSyncExecuted, $"第二周期も必要集合を生成し、exact同値planで保存を省略: generated={second.Lr2NormalFolderGeneratedCount}, upserted={second.Lr2NormalFolderUpsertedCount}, deleted={second.Lr2NormalFolderDeletedCount}");
            Assert.AreEqual(0, second.Lr2NormalFolderUpsertedCount + second.Lr2NormalFolderDeletedCount);
            CollectionAssert.AreEqual(scanPaths, scan.ChartFilePaths.ToArray());
            Assert.AreSame(folderInfoEntry, scan.FolderInfoFileEntriesByPath[folderInfoPath]);
            Assert.AreEqual(root, options.Lr2Input.RegisteredRoots.Single());
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
                lr2NormalFolderSyncRootDirectories: [lr2RootPath],
                folderTableSnapshot: CaptureNormalFolderSnapshot(songDbPath));

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
                lr2NormalFolderSyncRootDirectories: [lr2RootPath],
                folderTableSnapshot: CaptureNormalFolderSnapshot(songDbPath));

            Assert.IsTrue(result.Lr2NormalFolderSyncExecuted);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder root = verify.Table<LR2SongDB.folder>().Single(row => row.path == ToFolderPath(lr2RootPath));
            LR2SongDB.folder pack = verify.Table<LR2SongDB.folder>().Single(row => row.path == ToFolderPath(packDirectoryPath));
            Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(rootSurfaceTimestamp), root.date);
            Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(packSurfaceTimestamp), pack.date);
        });
    }

    /// <summary>
    /// 全rootの完全scanでは、削除譜面の親と別の古い通常folderを共に退役する。
    /// </summary>
    [TestMethod]
    public void ApplyFileScanDiff_PrunesAllStaleNormalFoldersInCompletedRoot()
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
                lr2NormalFolderSyncRootDirectories: [lr2RootPath],
                folderTableSnapshot: CaptureNormalFolderSnapshot(songDbPath));

            Assert.IsTrue(result.HasDbDiff);
            Assert.IsTrue(result.Lr2NormalFolderSyncExecuted);
            Assert.AreEqual(2, result.Lr2NormalFolderDeletedCount);
            Assert.IsTrue(logs.Any(message => message.Contains("lr2_normal_folder_sync done")
                && message.Contains("directoryPaths=2")
                && message.Contains("pruneScopes=1")));

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", removedFolderPath));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", staleOtherFolderPath));
        });
    }

    /// <summary>
    /// 完全scanで不要なnested folderと古い兄弟を退役し、譜面の残る祖先は必要集合として保持する。
    /// </summary>
    [TestMethod]
    public void ApplyFileScanDiff_DeletedNestedBmsKeepsRequiredAncestorAndPrunesStaleSibling()
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
                lr2NormalFolderSyncRootDirectories: [lr2RootPath],
                folderTableSnapshot: CaptureNormalFolderSnapshot(songDbPath));

            Assert.IsTrue(result.HasDbDiff);
            Assert.IsTrue(result.Lr2NormalFolderSyncExecuted);
            Assert.AreEqual(2, result.Lr2NormalFolderDeletedCount);
            Assert.IsTrue(logs.Any(message => message.Contains("lr2_normal_folder_sync done")
                && message.Contains("directoryPaths=3")
                && message.Contains("pruneScopes=1")));

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", removedFolderPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", bigFolderPath));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", staleSiblingFolderPath));
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
                lr2NormalFolderSyncRootDirectories: [lr2RootPath],
                folderTableSnapshot: CaptureNormalFolderSnapshot(songDbPath));

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
    /// root直下BMSの削除後も全必要集合を生成し、同root内の古い兄弟folder行を退役する。
    /// </summary>
    [TestMethod]
    public void ApplyFileScanDiff_DeletedRootLevelBmsPrunesStaleFolderInCompletedRoot()
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
                lr2NormalFolderSyncRootDirectories: [lr2RootPath],
                folderTableSnapshot: CaptureNormalFolderSnapshot(songDbPath));

            Assert.IsTrue(result.HasDbDiff);
            Assert.IsTrue(result.Lr2NormalFolderSyncExecuted);
            Assert.IsFalse(result.Lr2NormalFolderSyncFailed);
            Assert.AreEqual(1, result.Lr2NormalFolderDeletedCount);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", staleFolderPath));
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
                lr2NormalFolderSyncRootDirectories: [lr2RootPath],
                folderTableSnapshot: CaptureNormalFolderSnapshot(songDbPath));

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
    /// 譜面とdirectoryのmtimeに差分がなくても、完全scanの必要集合にない同root内の旧folder行を退役する。
    /// </summary>
    [TestMethod]
    public void ApplyFileScanDiff_PrunesStaleNormalFolderWithoutSongDbDiff()
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
                lr2NormalFolderSyncRootDirectories: [lr2RootPath],
                folderTableSnapshot: CaptureNormalFolderSnapshot(songDbPath));

            Assert.IsFalse(result.HasDbDiff);
            Assert.IsTrue(result.Lr2NormalFolderSyncExecuted);
            Assert.IsFalse(result.Lr2NormalFolderSyncFailed);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(2L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder;"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", stalePath));
        });
    }

    /// <summary>
    /// BMSのtxt有無だけが変わる周期でも、完全scanの必要集合にない同root内の旧folder行を退役する。
    /// </summary>
    [TestMethod]
    public void ApplyFileScanDiff_PrunesStaleNormalFolderWithBmsTextOnlyDiff()
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
                lr2NormalFolderSyncRootDirectories: [lr2RootPath],
                folderTableSnapshot: CaptureNormalFolderSnapshot(songDbPath));

            Assert.IsTrue(result.HasDbDiff);
            Assert.AreEqual(1, result.BmsTextOnlyUpdateCount);
            Assert.IsTrue(result.Lr2NormalFolderSyncExecuted);
            Assert.IsFalse(result.Lr2NormalFolderSyncFailed);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", stalePath));
        });
    }

}
