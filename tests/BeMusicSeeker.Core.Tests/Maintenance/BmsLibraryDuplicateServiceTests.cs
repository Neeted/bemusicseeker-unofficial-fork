using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryDuplicateServiceTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryDuplicateServiceTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>
    /// 二種類の重複MD5で三フォルダが連結する入力から一群と四譜面を抽出し、間接的な連結の分断と重複警告の欠落を検出します。
    /// </summary>
    [TestMethod]
    public void Analyze_GroupsDirectoriesConnectedByDuplicateHashes()
    {

        var service = new BmsLibraryDuplicateService();
        List<ChartFile> files =
        [
            CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\BMS", "DirA", "a.bms")),
            CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\BMS", "DirB", "a.bms")),
            CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\BMS", "DirB", "b.bms")),
            CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\BMS", "DirC", "b.bms"))
        ];

        DuplicateAnalysisResult result = service.Analyze(CreateDuplicateAnalysisSnapshot(files, []), Resources.Warning_DuplicateBmsFile);

        Assert.AreEqual(1, result.DuplicateGroups.Count);
        CollectionAssert.AreEquivalent(new[] { "C:\\BMS\\DirA", "C:\\BMS\\DirB", "C:\\BMS\\DirC" }, result.DuplicateGroups[0].Folders);
        Assert.AreEqual(4, result.DuplicateCharts.Count);
        Assert.AreEqual(3, result.ConnectedDirectoryCount);
        Assert.IsTrue(result.DuplicateGroups[0].ChartFiles.All(chart => chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.DuplicateChart)));
    }

    /// <summary>
    /// 同じ譜面へ重複警告を二回適用しても強調と一件の表示要約を保ち、警告の二重追加を検出します。
    /// </summary>
    [TestMethod]
    public void ApplyDuplicateWarnings_SetsStructuredWarningWithoutDuplicates()
    {

        var service = new BmsLibraryDuplicateService();
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\BMS", "DirA", "a.bms"));

        file = service.ApplyDuplicateWarnings([file], Resources.Warning_DuplicateBmsFile).Single();
        file = service.ApplyDuplicateWarnings([file], Resources.Warning_DuplicateBmsFile).Single();

        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.DuplicateChart));
        Assert.IsTrue(ChartWarningCollection.HasAnyHighlightedWarning(file.Warnings));
        Assert.AreEqual("[1] 重複譜面", ChartWarningCollection.BuildDigestText(file.Warnings, file.InstallDestination));
    }

    /// <summary>
    /// BMSとBMSONのMD5一致だけを重複群へ含め、SHA256だけの一致や別MD5を誤って統合する判断を検出します。
    /// </summary>
    [TestMethod]
    public void Analyze_BuildSnapshotIncludesBmsonAndUsesMd5PrimaryLookupHash()
    {

        var service = new BmsLibraryDuplicateService();
        List<ChartFile> bmsFiles =
        [
            CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\BMS", "DirA", "a.bms"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            CreateFile("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", Path.Combine("C:\\BMS", "DirB", "b.bms"), "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"),
            CreateFile(null, Path.Combine("C:\\BMS", "DirE", "e.bms"), "9999999999999999999999999999999999999999999999999999999999999999")
        ];
        List<ChartFile> bmsonSongs =
        [
            (ChartTestValues.Empty(ChartFileKind.Bmson) with { Token = new OwnedChartToken() }) with {
                Path = Path.Combine("C:\\BMS", "DirC", "c.bmson"),
                RawTitle = "bmson md5 duplicate",
                Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                Sha256 = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"
            },
            (ChartTestValues.Empty(ChartFileKind.Bmson) with { Token = new OwnedChartToken() }) with {
                Path = Path.Combine("C:\\BMS", "DirD", "d.bmson"),
                RawTitle = "sha256 only does not duplicate",
                Sha256 = "9999999999999999999999999999999999999999999999999999999999999999"
            },
            (ChartTestValues.Empty(ChartFileKind.Bmson) with { Token = new OwnedChartToken() }) with {
                Path = Path.Combine("C:\\BMS", "DirF", "f.bmson"),
                RawTitle = "md5 wins over sha256",
                Md5 = "ffffffffffffffffffffffffffffffff",
                Sha256 = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
            }
        ];

        OwnedDuplicateChartRowSnapshot snapshot = CreateDuplicateAnalysisSnapshot(bmsFiles, bmsonSongs);

        DuplicateAnalysisResult result = service.Analyze(snapshot, Resources.Warning_DuplicateBmsFile);

        Assert.AreEqual(1, result.DuplicateGroups.Count);
        Assert.AreEqual(1, snapshot.DuplicateHashCount);
        Assert.AreEqual(2, snapshot.DuplicateHashRowCount);
        Assert.AreEqual(2, result.ConnectedDirectoryCount);
        Assert.IsTrue(result.DuplicateGroups.Any(group => group.Folders.Count == 2 && group.Folders.Contains("C:\\BMS\\DirA") && group.Folders.Contains("C:\\BMS\\DirC")));
        Assert.IsTrue(result.DuplicateGroups.SelectMany(group => group.ChartFiles).Any(chart => chart.Kind == ChartFileKind.Bmson));
        Assert.IsTrue(result.DuplicateGroups.SelectMany(group => group.ChartFiles).Where(chart => chart.Kind == ChartFileKind.Bmson).All(chart => chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.DuplicateChart)));
        Assert.IsFalse(result.DuplicateGroups.Any(group => group.Folders.Contains("C:\\BMS\\DirB") || group.Folders.Contains("C:\\BMS\\DirD") || group.Folders.Contains("C:\\BMS\\DirE") || group.Folders.Contains("C:\\BMS\\DirF")));
        Assert.AreEqual(2, result.DuplicateCharts.Count);
    }

    /// <summary>
    /// 重複群のフォルダに固有譜面を同梱しても、重複MD5を持つ二譜面だけへ警告を付けることを確認します。
    /// </summary>
    [TestMethod]
    public void Analyze_DuplicateWarningTargetsOnlyRowsWithDuplicateHash()
    {

        var service = new BmsLibraryDuplicateService();
        ChartFile duplicateBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\BMS", "DirA", "a.bms"));
        ChartFile uniqueSibling = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\BMS", "DirA", "unique.bms"));
        ChartFile duplicateBmson = (ChartTestValues.Empty(ChartFileKind.Bmson) with { Token = new OwnedChartToken() }) with
        {
            Path = Path.Combine("C:\\BMS", "DirB", "b.bmson"),
            RawTitle = "bmson duplicate",
            Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        };

        DuplicateAnalysisResult result = service.Analyze(
            CreateDuplicateAnalysisSnapshot([duplicateBms, uniqueSibling], [duplicateBmson]),
            Resources.Warning_DuplicateBmsFile);

        Assert.AreEqual(1, result.DuplicateGroups.Count);
        Assert.AreEqual(2, result.DuplicateCharts.Count);
        Assert.IsTrue(result.DuplicateCharts.Any(chart => ReferenceEquals(chart.Token, duplicateBms.Token)));
        Assert.IsFalse(result.DuplicateCharts.Any(chart => ReferenceEquals(chart.Token, uniqueSibling.Token)));
        ChartFile uniqueChart = result.DuplicateGroups[0].ChartFiles.Single(chart => string.Equals(chart.Path, uniqueSibling.Path, System.StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(uniqueChart.Warnings.Any(warning => warning.Kind == ChartWarningKind.DuplicateChart));
        ChartFile duplicateBmsonChart = result.DuplicateGroups[0].ChartFiles.Single(chart => chart.Kind == ChartFileKind.Bmson);
        Assert.IsTrue(duplicateBmsonChart.Warnings.Any(warning => warning.Kind == ChartWarningKind.DuplicateChart));
    }

    /// <summary>
    /// 所持スナップショットから重複群と同梱譜面だけを具体化し、無関係な譜面の具体化とリソースの先行解析を検出します。
    /// </summary>
    [TestMethod]
    public void Analyze_OwnedCollectionSnapshotMaterializesOnlyGroupedRows()
    {

        var service = new BmsLibraryDuplicateService();
        ChartFile duplicateBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\BMS", "DirA", "a.bms"));
        ChartFile uniqueSibling = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\BMS", "DirA", "unique.bms"));
        ChartFile unrelated = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\BMS", "DirC", "c.bms"));
        ChartFile duplicateBmson = (ChartTestValues.Empty(ChartFileKind.Bmson) with { Token = new OwnedChartToken() }) with
        {
            Path = Path.Combine("C:\\BMS", "DirB", "b.bmson"),
            RawTitle = "bmson duplicate",
            Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        };
        var ownedCharts = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([duplicateBms, uniqueSibling, unrelated], [duplicateBmson]));

        OwnedDuplicateChartRowSnapshot duplicateSnapshot = ownedCharts.CreateDuplicateChartRowSnapshot();
        IReadOnlyList<DuplicateChartRow> snapshotRows = duplicateSnapshot.Rows;
        Assert.AreEqual(4, snapshotRows.Count(row => row.Chart != null));
        Assert.IsTrue(snapshotRows.All(row => row.Chart.Resources == null), "重複検索でリソースを先行解析しません。");
        CollectionAssert.AreEquivalent(new[] { duplicateBms, uniqueSibling, unrelated }, duplicateSnapshot.BmsCharts.ToArray());
        Assert.AreEqual(1, duplicateSnapshot.DuplicateHashCount);
        Assert.AreEqual(2, duplicateSnapshot.DuplicateHashRowCount);

        DuplicateAnalysisResult result = service.Analyze(duplicateSnapshot, Resources.Warning_DuplicateBmsFile);

        Assert.AreEqual(4, snapshotRows.Count(row => row.Chart != null));
        Assert.IsTrue(snapshotRows.All(row => row.Chart.Resources == null), "重複検索でリソースを先行解析しません。");
        Assert.AreEqual(4, snapshotRows.Count);
        Assert.AreEqual(3, result.MaterializedChartCount);
        Assert.AreEqual(2, result.ConnectedDirectoryCount);
        Assert.AreEqual(1, result.DuplicateGroups.Count);
        Assert.AreEqual(2, result.DuplicateCharts.Count);
        Assert.IsTrue(result.DuplicateCharts.Any(chart => ReferenceEquals(chart.Token, duplicateBms.Token)));
        Assert.IsFalse(result.DuplicateCharts.Any(chart => ReferenceEquals(chart.Token, uniqueSibling.Token)));
        Assert.IsFalse(result.DuplicateGroups[0].ChartFiles.Any(chart => string.Equals(chart.Path, unrelated.Path, System.StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(result.DuplicateGroups[0].ChartFiles.Single(chart => chart.Kind == ChartFileKind.Bmson).Warnings.Any(warning => warning.Kind == ChartWarningKind.DuplicateChart));
    }

    /// <summary>
    /// 同一フォルダの大文字小文字違いを含む重複入力でも固有の同梱譜面を同群へ含め、パス表記による分断を検出します。
    /// </summary>
    [TestMethod]
    public void Analyze_DuplicateConnectedDirectoryMatchingIgnoresPathCasing()
    {

        var service = new BmsLibraryDuplicateService();
        ChartFile duplicateLower = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\BMS", "DirA", "a.bms"));
        ChartFile duplicateUpper = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\BMS", "DIRA", "b.bms"));
        ChartFile uniqueSibling = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\BMS", "dira", "unique.bms"));

        DuplicateAnalysisResult result = service.Analyze(
            CreateDuplicateAnalysisSnapshot([duplicateLower, duplicateUpper, uniqueSibling], []),
            Resources.Warning_DuplicateBmsFile);

        Assert.AreEqual(1, result.DuplicateGroups.Count);
        Assert.AreEqual(3, result.MaterializedChartCount);
        Assert.AreEqual(2, result.DuplicateCharts.Count);
        Assert.IsTrue(result.DuplicateGroups[0].ChartFiles.Any(chart => string.Equals(chart.Path, uniqueSibling.Path, System.StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// nullまたは空のパスを持つBMSONから重複行を作らず、識別できない譜面の重複索引への混入を検出します。
    /// </summary>
    [TestMethod]
    public void BuildSnapshot_SkipsBmsonRowsWithoutPath()
    {
        List<ChartFile> bmsonSongs =
        [
            ChartTestValues.Empty(ChartFileKind.Bmson) with {
                Path = null,
                RawTitle = "missing path",
                Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            },
            ChartTestValues.Empty(ChartFileKind.Bmson) with {
                Path = string.Empty,
                RawTitle = "blank path",
                Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            }
        ];

        OwnedDuplicateChartRowSnapshot snapshot = CreateDuplicateAnalysisSnapshot([], bmsonSongs);

        Assert.AreEqual(0, snapshot.Rows.Count);
        Assert.AreEqual(0, snapshot.DuplicateHashCount);
    }

    /// <summary>
    /// 実譜面衝突と保護readmeの将来通常先を含む統合で、全bytes・既存利用者列・採番実pathのDB/所持と一session確定・回収を保ちます。
    /// </summary>
    /// <remarks>実ファイルとSQLiteを通す確定・失敗境界を観測するため、局所フォルダと実ライブラリを使用します。</remarks>
    [TestMethod]
    public void MergeChartDirectory_DifferentContentCollisionUsesActualDestinationEverywhere()
    {

        WithTemporarySongDb(databaseScope, delegate (string songDbPath)
        {
            using var caseWorkspace1 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
            string tempRootPath = Path.Combine(
                caseWorkspace1.RootDirectory,
                "BeMusicSeeker_DuplicateMergeBmsCollision_" + System.Guid.NewGuid().ToString("N"));
            string srcDir = Path.Combine(tempRootPath, "Src");
            string dstDir = Path.Combine(tempRootPath, "Dst");
            string srcChartPath = Path.Combine(srcDir, "chart.bms");
            string collisionPath = Path.Combine(dstDir, "chart.bms");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(dstDir);
            File.WriteAllText(srcChartPath, "#PLAYER 1\r\n#TITLE merge source\r\n#ARTIST source\r\n#00111:01\r\n");
            File.WriteAllText(collisionPath, "#PLAYER 1\r\n#TITLE existing destination\r\n#ARTIST existing\r\n#00111:02\r\n");
            byte[] sourceBytes = File.ReadAllBytes(srcChartPath);
            byte[] collisionBytes = File.ReadAllBytes(collisionPath);
            File.WriteAllBytes(Path.Combine(dstDir, "readme.txt"), [1]);
            File.WriteAllBytes(Path.Combine(srcDir, "readme.txt"), [2, 3]);
            File.WriteAllBytes(Path.Combine(srcDir, "readme(1).txt"), [4, 5, 6]);
            File.SetLastWriteTimeUtc(Path.Combine(dstDir, "readme.txt"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(Path.Combine(srcDir, "readme.txt"), new DateTime(2020, 1, 3, 0, 0, 0, DateTimeKind.Utc));
            TestBmsLibrary? library = null;
            try
            {
                ChartFile sourceFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(srcChartPath));
                ChartFile existingFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(collisionPath));
                existingFile = existingFile with { Favorite = 7 };
                existingFile = existingFile with { AddDate = 12345 };
                existingFile = existingFile with { Tag = "existing-user-tag" };
                using (LR2SongDBExtended songDb = databaseScope.OpenSongWrite(songDbPath))
                {
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(sourceFile), typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(existingFile), typeof(LR2SongDB.song));
                }

                library = new TestBmsLibrary(databaseScope,
                    songDbPath,
                    null,
                    null,
                    new TestFileMutationService(),
                    new RecordingDialogService(),
                    new TestUiScheduler(() => null),
                    () => new BmsLibraryOptionsSnapshot
                    {
                        OperationModeLR2DB = false,
                        EnableSmartComponentOverwrite = true,
                        KeepSmartOverwriteProtectedFilesByRenaming = true,
                        DeletePendingPackageSourceAfterInstall = false
                    })
                {
                    BmsCharts = [sourceFile, existingFile],
                    BmsonCharts = []
                };

                DuplicateMergeMaintenanceReceipt receipt = library.MergeChartDirectory(srcDir, dstDir, operationId: 1);

                Assert.IsTrue(receipt.MergeApplied);
                Assert.IsNotNull(receipt.SessionReceipt);
                Assert.IsTrue(receipt.SessionReceipt.DurableCommit);
                Assert.AreEqual(1, receipt.SessionReceipt.ConfirmedChangeCount);
                Assert.AreEqual(1, receipt.SessionReceipt.CatalogChartPathChangeCount);
                Assert.AreEqual(dstDir, receipt.SessionReceipt.ConfirmedTargets.Single().DestinationPath);
                string actualDestinationPath = Directory.EnumerateFiles(dstDir, "*.bms").Single(path =>
                    !string.Equals(path, collisionPath, StringComparison.Ordinal));

                Assert.AreEqual(srcChartPath, sourceFile.Path);
                Assert.IsTrue(library.BmsCharts.Any(chart => chart.Path == actualDestinationPath));
                Assert.IsFalse(File.Exists(srcChartPath));
                Assert.IsTrue(File.Exists(collisionPath));
                Assert.IsTrue(File.Exists(actualDestinationPath));
                CollectionAssert.AreEqual(collisionBytes, File.ReadAllBytes(collisionPath));
                CollectionAssert.AreEqual(sourceBytes, File.ReadAllBytes(actualDestinationPath));
                Assert.IsNotNull(library.BmsCharts.SingleOrDefault(file =>
                    string.Equals(file.Path, collisionPath, StringComparison.Ordinal)));
                Assert.IsNotNull(library.BmsCharts.SingleOrDefault(file =>
                    string.Equals(file.Path, actualDestinationPath, StringComparison.Ordinal)));

                using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
                LR2SongDB.song existingRow = verify.Find<LR2SongDB.song>(collisionPath);
                Assert.IsNotNull(existingRow);
                Assert.AreEqual(existingFile.Md5, existingRow.hash);
                Assert.AreEqual(7, existingRow.favorite);
                Assert.AreEqual(12345, existingRow.adddate);
                Assert.AreEqual("existing-user-tag", existingRow.tag);
                LR2SongDB.song movedRow = verify.Find<LR2SongDB.song>(actualDestinationPath);
                Assert.IsNotNull(movedRow);
                Assert.AreEqual(sourceFile.Md5, movedRow.hash);
                Assert.IsNull(verify.Find<LR2SongDB.song>(srcChartPath));
                CollectionAssert.AreEqual(new byte[] { 1 }, File.ReadAllBytes(Path.Combine(dstDir, "readme.txt")));
                CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, File.ReadAllBytes(Path.Combine(dstDir, "readme(1).txt")));
                CollectionAssert.AreEqual(new byte[] { 2, 3 }, File.ReadAllBytes(Path.Combine(dstDir, "readme(2).txt")));
                CollectionAssert.AreEquivalent(new[] { collisionPath, actualDestinationPath, Path.Combine(dstDir, "readme.txt"),
                    Path.Combine(dstDir, "readme(1).txt"), Path.Combine(dstDir, "readme(2).txt") }, Directory.GetFiles(dstDir, "*", System.IO.SearchOption.AllDirectories));
                Assert.IsFalse(Directory.Exists(srcDir));
                Assert.IsTrue(library.OperationAdmission.TryEnter(out IDisposable admission));
                admission.Dispose();
            }
            finally
            {
                library?.RequestShutdown("protected-name-merge");
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    private static OwnedDuplicateChartRowSnapshot CreateDuplicateAnalysisSnapshot(
        IEnumerable<ChartFile> bmsFiles,
        IEnumerable<ChartFile> bmsonSongs)
    {
        List<ChartFile> bmsFileList = [.. (bmsFiles ?? []).Where(file => file != null)];
        List<DuplicateChartRow> rows = [.. new[]
            {
                bmsFileList
                    .Select(file => (file)),
                (bmsonSongs ?? [])
                    .Where(song => song != null)
                    .Select(song => (song))
            }
            .SelectMany(charts => charts)
            .Select(DuplicateChartRow.CreateFromChart)
            .Where(row => row != null)];
        return new OwnedDuplicateChartRowSnapshot(rows, bmsFileList);
    }

}
