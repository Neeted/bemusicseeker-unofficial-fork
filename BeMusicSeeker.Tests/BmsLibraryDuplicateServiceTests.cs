using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Microsoft.VisualBasic.FileIO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryDuplicateServiceTests
{
    [TestMethod]
    public void Analyze_GroupsDirectoriesConnectedByDuplicateHashes()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryDuplicateService();
        List<BMSFile> files =
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

    [TestMethod]
    public void ApplyDuplicateWarnings_SetsStructuredWarningWithoutDuplicates()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryDuplicateService();
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\BMS", "DirA", "a.bms"));

        service.ApplyDuplicateWarnings([ChartFileProjection.FromBmsFile(file)], Resources.Warning_DuplicateBmsFile);
        service.ApplyDuplicateWarnings([ChartFileProjection.FromBmsFile(file)], Resources.Warning_DuplicateBmsFile);

        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.DuplicateChart));
        Assert.IsTrue(file.Warnings.HasHighlightedWarning);
        Assert.AreEqual("[1] 重複譜面", file.Warnings.BuildDigestText());
    }

    [TestMethod]
    public void ClearDuplicateState_RemovesStructuredDuplicateWarningsOnly()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryDuplicateService();
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\BMS", "DirA", "a.bms"));
        file.SetWarning(ChartWarningKind.DuplicateChart, Resources.Warning_DuplicateBmsFile);
        file.SetWarning(ChartWarningKind.NestedChartFileInPackage, Resources.Warning_NestedChartFileInPackage);

        service.ClearDuplicateState([file]);

        Assert.IsFalse(file.Warnings.Contains(ChartWarningKind.DuplicateChart));
        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.NestedChartFileInPackage));
        Assert.AreEqual("[1] サブフォルダ譜面", file.Warnings.BuildDigestText());
    }

    [TestMethod]
    public void Analyze_BuildSnapshotIncludesBmsonAndUsesMd5PrimaryLookupHash()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryDuplicateService();
        List<BMSFile> bmsFiles =
        [
            CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\BMS", "DirA", "a.bms"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            CreateFile("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", Path.Combine("C:\\BMS", "DirB", "b.bms"), "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"),
            CreateFile(null, Path.Combine("C:\\BMS", "DirE", "e.bms"), "9999999999999999999999999999999999999999999999999999999999999999")
        ];
        List<LR2SongDBExtended.bmson_song> bmsonSongs =
        [
            new LR2SongDBExtended.bmson_song
            {
                path = Path.Combine("C:\\BMS", "DirC", "c.bmson"),
                title = "bmson md5 duplicate",
                md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                sha256 = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"
            },
            new LR2SongDBExtended.bmson_song
            {
                path = Path.Combine("C:\\BMS", "DirD", "d.bmson"),
                title = "sha256 only does not duplicate",
                sha256 = "9999999999999999999999999999999999999999999999999999999999999999"
            },
            new LR2SongDBExtended.bmson_song
            {
                path = Path.Combine("C:\\BMS", "DirF", "f.bmson"),
                title = "md5 wins over sha256",
                md5 = "ffffffffffffffffffffffffffffffff",
                sha256 = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
            }
        ];

        OwnedDuplicateChartRowSnapshot snapshot = CreateDuplicateAnalysisSnapshot(bmsFiles, bmsonSongs);

        DuplicateAnalysisResult result = service.Analyze(snapshot, Resources.Warning_DuplicateBmsFile);

        Assert.AreEqual(1, result.DuplicateGroups.Count);
        Assert.AreEqual(1, snapshot.DuplicateHashCount);
        Assert.AreEqual(2, snapshot.DuplicateHashRowCount);
        Assert.AreEqual(2, result.ConnectedDirectoryCount);
        Assert.IsTrue(result.DuplicateGroups.Any(group => group.Folders.Count == 2 && group.Folders.Contains("C:\\BMS\\DirA") && group.Folders.Contains("C:\\BMS\\DirC")));
        Assert.IsTrue(result.DuplicateGroups.SelectMany(group => group.ChartFiles).Any(chart => chart.Kind == ChartFileKind.Bmson && chart.GetBmsonStorageOwner() != null));
        Assert.IsTrue(result.DuplicateGroups.SelectMany(group => group.ChartFiles).Where(chart => chart.Kind == ChartFileKind.Bmson).All(chart => chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.DuplicateChart)));
        Assert.IsFalse(result.DuplicateGroups.Any(group => group.Folders.Contains("C:\\BMS\\DirB") || group.Folders.Contains("C:\\BMS\\DirD") || group.Folders.Contains("C:\\BMS\\DirE") || group.Folders.Contains("C:\\BMS\\DirF")));
        Assert.AreEqual(2, result.DuplicateCharts.Count);
    }

    [TestMethod]
    public void Analyze_DuplicateWarningTargetsOnlyRowsWithDuplicateHash()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryDuplicateService();
        BMSFile duplicateBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\BMS", "DirA", "a.bms"));
        BMSFile uniqueSibling = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\BMS", "DirA", "unique.bms"));
        var duplicateBmson = new LR2SongDBExtended.bmson_song
        {
            path = Path.Combine("C:\\BMS", "DirB", "b.bmson"),
            title = "bmson duplicate",
            md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        };

        DuplicateAnalysisResult result = service.Analyze(
            CreateDuplicateAnalysisSnapshot([duplicateBms, uniqueSibling], [duplicateBmson]),
            Resources.Warning_DuplicateBmsFile);

        Assert.AreEqual(1, result.DuplicateGroups.Count);
        Assert.AreEqual(2, result.DuplicateCharts.Count);
        Assert.IsTrue(result.DuplicateCharts.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), duplicateBms)));
        Assert.IsFalse(result.DuplicateCharts.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), uniqueSibling)));
        ChartFile uniqueChart = result.DuplicateGroups[0].ChartFiles.Single(chart => string.Equals(chart.Path, uniqueSibling.path, System.StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(uniqueChart.Warnings.Any(warning => warning.Kind == ChartWarningKind.DuplicateChart));
        ChartFile duplicateBmsonChart = result.DuplicateGroups[0].ChartFiles.Single(chart => chart.Kind == ChartFileKind.Bmson);
        Assert.IsTrue(duplicateBmsonChart.Warnings.Any(warning => warning.Kind == ChartWarningKind.DuplicateChart));
    }

    [TestMethod]
    public void Analyze_OwnedCollectionSnapshotMaterializesOnlyGroupedRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryDuplicateService();
        BMSFile duplicateBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\BMS", "DirA", "a.bms"));
        BMSFile uniqueSibling = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\BMS", "DirA", "unique.bms"));
        BMSFile unrelated = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\BMS", "DirC", "c.bms"));
        var duplicateBmson = new LR2SongDBExtended.bmson_song
        {
            path = Path.Combine("C:\\BMS", "DirB", "b.bmson"),
            title = "bmson duplicate",
            md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        };
        OwnedChartCollectionState ownedCharts = OwnedChartCollectionState.FromStorageRows([duplicateBms, uniqueSibling, unrelated], [duplicateBmson]);

        OwnedDuplicateChartRowSnapshot duplicateSnapshot = ownedCharts.CreateDuplicateChartRowSnapshot();
        IReadOnlyList<DuplicateChartRow> snapshotRows = duplicateSnapshot.Rows;
        Assert.AreEqual(0, snapshotRows.Count(row => row.Chart != null));
        CollectionAssert.AreEquivalent(new[] { duplicateBms, uniqueSibling, unrelated }, duplicateSnapshot.BmsStorageRows.ToArray());
        Assert.AreEqual(1, duplicateSnapshot.DuplicateHashCount);
        Assert.AreEqual(2, duplicateSnapshot.DuplicateHashRowCount);

        DuplicateAnalysisResult result = service.Analyze(duplicateSnapshot, Resources.Warning_DuplicateBmsFile);

        Assert.AreEqual(0, snapshotRows.Count(row => row.Chart != null));
        Assert.AreEqual(4, snapshotRows.Count);
        Assert.AreEqual(3, result.MaterializedChartCount);
        Assert.AreEqual(2, result.ConnectedDirectoryCount);
        Assert.AreEqual(1, result.DuplicateGroups.Count);
        Assert.AreEqual(2, result.DuplicateCharts.Count);
        Assert.IsTrue(result.DuplicateCharts.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), duplicateBms)));
        Assert.IsFalse(result.DuplicateCharts.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), uniqueSibling)));
        Assert.IsFalse(result.DuplicateGroups[0].ChartFiles.Any(chart => string.Equals(chart.Path, unrelated.path, System.StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(result.DuplicateGroups[0].ChartFiles.Single(chart => chart.Kind == ChartFileKind.Bmson).Warnings.Any(warning => warning.Kind == ChartWarningKind.DuplicateChart));
    }

    [TestMethod]
    public void Analyze_DuplicateConnectedDirectoryMatchingIgnoresPathCasing()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryDuplicateService();
        BMSFile duplicateLower = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\BMS", "DirA", "a.bms"));
        BMSFile duplicateUpper = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\BMS", "DIRA", "b.bms"));
        BMSFile uniqueSibling = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\BMS", "dira", "unique.bms"));

        DuplicateAnalysisResult result = service.Analyze(
            CreateDuplicateAnalysisSnapshot([duplicateLower, duplicateUpper, uniqueSibling], []),
            Resources.Warning_DuplicateBmsFile);

        Assert.AreEqual(1, result.DuplicateGroups.Count);
        Assert.AreEqual(3, result.MaterializedChartCount);
        Assert.AreEqual(2, result.DuplicateCharts.Count);
        Assert.IsTrue(result.DuplicateGroups[0].ChartFiles.Any(chart => string.Equals(chart.Path, uniqueSibling.path, System.StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void BuildSnapshot_SkipsBmsonRowsWithoutPath()
    {
        var service = new BmsLibraryDuplicateService();
        List<LR2SongDBExtended.bmson_song> bmsonSongs =
        [
            new LR2SongDBExtended.bmson_song
            {
                path = null,
                title = "missing path",
                md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            },
            new LR2SongDBExtended.bmson_song
            {
                path = string.Empty,
                title = "blank path",
                md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            }
        ];

        OwnedDuplicateChartRowSnapshot snapshot = CreateDuplicateAnalysisSnapshot([], bmsonSongs);

        Assert.AreEqual(0, snapshot.Rows.Count);
        Assert.AreEqual(0, snapshot.DuplicateHashCount);
    }

    [TestMethod]
    public void BmsonSongsSetter_InvalidatesDuplicateCache()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                DuplicateChartGroups =
                [
                    new DuplicateGroup(
                        [
                            ChartFileProjection.FromBmsFile(CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", @"C:\BMS\DirA\a.bms"))
                        ],
                        [@"C:\BMS\DirA"])
                ],

                BmsonSongs =
                [
                    new LR2SongDBExtended.bmson_song
                    {
                        path = @"C:\BMS\DirA\chart.bmson",
                        folder = @"C:\BMS\DirA",
                        md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                    }
                ]
            };

            Assert.IsNull(library.DuplicateChartGroups);
        });
    }

    [TestMethod]
    public void SearchDuplicateChartGroups_RebuildsAfterBmsonSongsChangeInvalidatesCache()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\BMS", "DirA", "a.bms"));
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BMSFiles =
                [
                    bmsFile
                ],
                BmsonSongs =
                [
                    new LR2SongDBExtended.bmson_song
                    {
                        path = Path.Combine("C:\\BMS", "DirB", "b.bmson"),
                        folder = Path.Combine("C:\\BMS", "DirB"),
                        title = "duplicate",
                        md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                    }
                ]
            };

            library.SearchDuplicateChartGroups();
            Assert.AreEqual(1, library.DuplicateChartGroups.Count);
            Assert.IsTrue(bmsFile.Warnings.Contains(ChartWarningKind.DuplicateChart));

            library.BmsonSongs = [];
            Assert.IsNull(library.DuplicateChartGroups);

            library.SearchDuplicateChartGroups();
            Assert.AreEqual(0, library.DuplicateChartGroups.Count);
            Assert.IsFalse(bmsFile.Warnings.Contains(ChartWarningKind.DuplicateChart));
        });
    }

    [TestMethod]
    public void SearchDuplicateChartGroups_FullClearsUntrackedDuplicateWarningsAfterBmsFilesReplacement()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            TestResourceInitializer.EnsureJapaneseResources();
            TestableBmsFile staleWarningFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\BMS", "DirA", "a.bms"));
            staleWarningFile.SetWarning(ChartWarningKind.DuplicateChart, Resources.Warning_DuplicateBmsFile);
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BMSFiles =
                [
                    staleWarningFile
                ],
                BmsonSongs = []
            };

            library.SearchDuplicateChartGroups();

            Assert.AreEqual(0, library.DuplicateChartGroups.Count);
            Assert.IsFalse(staleWarningFile.Warnings.Contains(ChartWarningKind.DuplicateChart));
        });
    }

    [TestMethod]
    public void RemoveLibraryCharts_BmsonPendingRow_UnregistersBmsonSong()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_DuplicateRemoveBmson_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRootPath);
            string chartPath = Path.Combine(tempRootPath, "chart.bmson");
            File.WriteAllText(chartPath, "{}");
            try
            {
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                var song = new LR2SongDBExtended.bmson_song
                {
                    path = chartPath,
                    folder = tempRootPath,
                    title = "duplicate",
                    md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                };
                library.BmsonSongs = [song];

                library.RemoveLibraryCharts([LibraryChartRef.FromChartFile(ChartFileProjection.FromBmsonSong(song))], sendToRecycleBin: false);

                Assert.IsFalse(File.Exists(chartPath));
                Assert.AreEqual(0, library.BmsonSongs.Count);
                using var songDb = new LR2SongDBExtended(songDbPath);
                Assert.IsNull(songDb.Find<LR2SongDBExtended.bmson_song>(chartPath));
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
    public void TryGetInstalledDirectoryByHash_ResolvesBmsonOnlyLibrary()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_BmsonInstalledDir_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRootPath);
            string chartPath = Path.Combine(tempRootPath, "chart.bmson");
            File.WriteAllText(chartPath, "{}");
            try
            {
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
                {
                    BmsonSongs =
                    [
                        new LR2SongDBExtended.bmson_song
                        {
                            path = chartPath,
                            folder = tempRootPath,
                            title = "bmson",
                            md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                            sha256 = new string('b', 64)
                        }
                    ]
                };

                bool resolved = library.TryGetInstalledDirectoryByHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", out string installDir);

                Assert.IsTrue(resolved);
                Assert.AreEqual(tempRootPath, installDir);
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
    public void MergeChartDirectory_BmsonOnly_ReRegistersSongAtDestination()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_DuplicateMergeBmson_" + System.Guid.NewGuid().ToString("N"));
            string srcDir = Path.Combine(tempRootPath, "Src");
            string dstDir = Path.Combine(tempRootPath, "Dst");
            string srcChartPath = Path.Combine(srcDir, "chart.bmson");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(dstDir);
            File.WriteAllText(srcChartPath, "{}");
            try
            {
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                var sourceSong = new LR2SongDBExtended.bmson_song
                {
                    path = srcChartPath,
                    folder = srcDir,
                    title = "merge target",
                    md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                };
                library.BmsonSongs = [sourceSong];
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(sourceSong, typeof(LR2SongDBExtended.bmson_song));
                }
                ResourceHealthIndexSnapshot beforeSnapshot = library.GetResourceHealthIndexSnapshotForView("duplicate_merge_before");
                Assert.AreEqual(1, beforeSnapshot.TargetCount);

                DuplicateMergeMaintenanceReceipt receipt = library.MergeChartDirectory(srcDir, dstDir, operationId: 1);

                Assert.IsTrue(receipt.MergeApplied);
                Assert.AreEqual(ResourceHealthIndexUpdateMode.DeferOnUpdates, receipt.IntermediateMode);
                Assert.IsTrue(receipt.MaintenanceHadUpdates);
                Assert.IsTrue(receipt.MaintenanceResult.HasUpdates);
                Assert.IsTrue(receipt.IntermediateDeferred);
                Assert.IsTrue(receipt.ResourceHealthIndexDeferred);
                Assert.IsTrue(receipt.MaintenanceResult.ResourceHealthIndexDeferred);
                Assert.IsFalse(receipt.ResourceHealthIndexDeltaApplied);
                Assert.IsFalse(receipt.MaintenanceResult.ResourceHealthIndexDeltaApplied);
                Assert.IsFalse(receipt.ResourceHealthIndexFullRebuilt);
                Assert.IsFalse(receipt.MaintenanceResult.ResourceHealthIndexFullRebuilt);
                Assert.AreSame(ResourceHealthIndexSnapshot.Empty, library.TryGetCurrentResourceHealthIndexSnapshotForView());

                string dstChartPath = Path.Combine(dstDir, "chart.bmson");
                Assert.IsFalse(File.Exists(srcChartPath));
                Assert.IsFalse(Directory.Exists(srcDir));
                Assert.IsTrue(File.Exists(dstChartPath));
                Assert.AreEqual(1, library.BmsonSongs.Count);
                Assert.AreEqual(dstChartPath, library.BmsonSongs[0].path);
                Assert.AreEqual(dstDir, library.BmsonSongs[0].folder);
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    Assert.IsNull(songDb.Find<LR2SongDBExtended.bmson_song>(srcChartPath));
                    Assert.IsNotNull(songDb.Find<LR2SongDBExtended.bmson_song>(dstChartPath));
                    Assert.IsTrue(songDb.Table<BMSFileMaintenanceInfo>().Any(info => info.path == dstChartPath));
                }
                ResourceHealthIndexSnapshot rebuiltSnapshot = library.GetResourceHealthIndexSnapshotForView("duplicate_merge_after");
                Assert.AreNotSame(beforeSnapshot, rebuiltSnapshot);
                Assert.AreNotEqual(beforeSnapshot.Version, rebuiltSnapshot.Version);
                ResourceHealthIndexSnapshot cachedSnapshot = library.GetResourceHealthIndexSnapshotForView("duplicate_merge_cached");
                Assert.AreSame(rebuiltSnapshot, cachedSnapshot);
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
    public void MergeChartDirectory_BmsChartPreservesExistingLr2SongUserColumns()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_DuplicateMergeBms_" + System.Guid.NewGuid().ToString("N"));
            string srcDir = Path.Combine(tempRootPath, "Src");
            string dstDir = Path.Combine(tempRootPath, "Dst");
            string srcChartPath = Path.Combine(srcDir, "chart.bms");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(dstDir);
            File.WriteAllText(srcChartPath, "#PLAYER 1\r\n#TITLE merge target\r\n#ARTIST artist\r\n#00111:01\r\n", Encoding.ASCII);
            try
            {
                BMSFile sourceFile = BMSFile.CreateBMSFileFromFile(srcChartPath);
                var existingRow = new TestableBmsFile
                {
                    path = srcChartPath,
                    adddate = 12345,
                    tag = "merge-user-tag"
                };
                existingRow.SetHash(sourceFile.hash);
                existingRow.SetFavorite(7);
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(existingRow, typeof(LR2SongDB.song));
                }

                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
                {
                    BMSFiles = [sourceFile],
                    BmsonSongs = []
                };

                library.MergeChartDirectory(srcDir, dstDir);

                string dstChartPath = Path.Combine(dstDir, "chart.bms");
                Assert.IsFalse(File.Exists(srcChartPath));
                Assert.IsFalse(Directory.Exists(srcDir));
                Assert.IsTrue(File.Exists(dstChartPath));
                Assert.AreEqual(1, library.BMSFiles.Count);
                Assert.AreEqual(dstChartPath, library.BMSFiles[0].path);
                using var verify = new LR2SongDBExtended(songDbPath);
                Assert.IsNull(verify.Find<LR2SongDB.song>(srcChartPath));
                LR2SongDB.song row = verify.Find<LR2SongDB.song>(dstChartPath);
                Assert.IsNotNull(row);
                Assert.AreEqual(sourceFile.hash, row.hash);
                Assert.AreEqual("merge target", row.title);
                Assert.AreEqual(7, row.favorite);
                Assert.AreEqual(12345, row.adddate);
                Assert.AreEqual("merge-user-tag", row.tag);
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
    public void MergeChartDirectory_BmsonDuplicateSkip_KeepsDestinationOnly()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_DuplicateMergeBmsonSkip_" + System.Guid.NewGuid().ToString("N"));
            string srcDir = Path.Combine(tempRootPath, "Src");
            string dstDir = Path.Combine(tempRootPath, "Dst");
            string srcChartPath = Path.Combine(srcDir, "chart.bmson");
            string dstChartPath = Path.Combine(dstDir, "chart.bmson");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(dstDir);
            File.WriteAllText(srcChartPath, "{}");
            File.WriteAllText(dstChartPath, "{}");
            string duplicateHash = BmsonSongParser.Parse(srcChartPath).md5;
            try
            {
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                var sourceSong = new LR2SongDBExtended.bmson_song
                {
                    path = srcChartPath,
                    folder = srcDir,
                    title = "src duplicate",
                    md5 = duplicateHash
                };
                var destinationSong = new LR2SongDBExtended.bmson_song
                {
                    path = dstChartPath,
                    folder = dstDir,
                    title = "dst duplicate",
                    md5 = duplicateHash
                };
                library.BmsonSongs = [sourceSong, destinationSong];
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(sourceSong, typeof(LR2SongDBExtended.bmson_song));
                    songDb.InsertOrReplace(destinationSong, typeof(LR2SongDBExtended.bmson_song));
                }

                library.MergeChartDirectory(srcDir, dstDir);

                Assert.IsFalse(Directory.Exists(srcDir));
                Assert.IsTrue(File.Exists(dstChartPath));
                Assert.AreEqual(1, library.BmsonSongs.Count);
                Assert.AreEqual(dstChartPath, library.BmsonSongs[0].path);
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    Assert.IsNull(songDb.Find<LR2SongDBExtended.bmson_song>(srcChartPath));
                    Assert.IsNotNull(songDb.Find<LR2SongDBExtended.bmson_song>(dstChartPath));
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

    private static TestableBmsFile CreateFile(string? hash, string path, string? sha256 = null)
    {
        var file = new TestableBmsFile
        {
            path = path
        };
        file.SetHash(hash);
        file.SetSha256(sha256);
        return file;
    }

    private static OwnedDuplicateChartRowSnapshot CreateDuplicateAnalysisSnapshot(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        List<BMSFile> bmsFileList = [.. (bmsFiles ?? []).Where(file => file != null)];
        List<DuplicateChartRow> rows = [.. new[]
            {
                bmsFileList
                    .Select(file => ChartFileProjection.FromBmsFile(file)),
                (bmsonSongs ?? [])
                    .Where(song => song != null)
                    .Select(song => ChartFileProjection.FromBmsonSong(song))
            }
            .SelectMany(charts => charts)
            .Select(DuplicateChartRow.CreateFromChart)
            .Where(row => row != null)];
        return new OwnedDuplicateChartRowSnapshot(rows, bmsFileList);
    }

    private static void WithTemporarySongDb(System.Action<string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_DuplicateTests_" + System.Guid.NewGuid().ToString("N"));
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
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
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
        public void SetHash(string? value)
        {
            hash = value;
        }

        public void SetSha256(string? value)
        {
            sha256 = value;
        }

        public void SetFavorite(int? value)
        {
            favorite = value;
        }
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
            string destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }
            if (File.Exists(destinationPath))
            {
                if (!overwrite)
                {
                    throw new IOException("Destination file already exists.");
                }
                File.Delete(destinationPath);
            }
            File.Move(sourcePath, destinationPath);
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            string destinationParent = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationParent))
            {
                Directory.CreateDirectory(destinationParent);
            }
            if (Directory.Exists(destinationPath))
            {
                if (!overwrite)
                {
                    throw new IOException("Destination directory already exists.");
                }
                Directory.Delete(destinationPath, recursive: true);
            }
            Directory.Move(sourcePath, destinationPath);
        }

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            string destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }
            File.Copy(sourcePath, destinationPath, overwrite);
        }

        public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            Directory.CreateDirectory(destinationPath);
            foreach (string directoryPath in Directory.GetDirectories(sourcePath, "*", System.IO.SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(directoryPath.Replace(sourcePath, destinationPath, System.StringComparison.OrdinalIgnoreCase));
            }
            foreach (string filePath in Directory.GetFiles(sourcePath, "*", System.IO.SearchOption.AllDirectories))
            {
                string destinationFilePath = filePath.Replace(sourcePath, destinationPath, System.StringComparison.OrdinalIgnoreCase);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath)!);
                File.Copy(filePath, destinationFilePath, overwrite);
            }
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

        public void SetTimestamps(string path, bool isDirectory, System.DateTime? creationTime, System.DateTime? lastWriteTime, FileMutationOptions options = null!)
        {
        }
    }
}
