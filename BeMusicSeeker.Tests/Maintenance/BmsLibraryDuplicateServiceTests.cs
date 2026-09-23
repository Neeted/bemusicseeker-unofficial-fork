using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualBasic.FileIO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

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
        var ownedCharts = OwnedChartCollectionState.FromStorageRows([duplicateBms, uniqueSibling, unrelated], [duplicateBmson]);

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

                LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts([LibraryChartRef.FromChartFile(ChartFileProjection.FromBmsonSong(song))], sendToRecycleBin: false);
                Assert.AreEqual(1, outcome.ConfirmedChartCount);
                Assert.IsTrue(outcome.CatalogDurable);
                Assert.IsFalse(outcome.HasError);

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

    /// <summary>
    /// merge 予約解放後の maintenance は BMS/BMSON の移動先 resource を反映します。
    /// DB 拒否・予約競合でも確定済み merge を戻さず、同じ session 終端へ必須処理失敗を残します。
    /// </summary>
    /// <param name="bmson">BMSON と BMS の双方で同じ確定・失敗境界を確認します。</param>
    /// <param name="maintenanceOutcome">0: 正常、1: maintenance DB 書込拒否、2: 予約競合。</param>
    [DataTestMethod]
    [DataRow(false, 0)]
    [DataRow(true, 0)]
    [DataRow(false, 1)]
    [DataRow(true, 2)]
    public void MergeChartDirectory_RechecksResourcesAfterReleasingMutationReservation(bool bmson, int maintenanceOutcome)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string sourceDirectory = Path.Combine(root, "Source");
            string destinationDirectory = Path.Combine(root, "Destination");
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(destinationDirectory);
            string sourcePath = Path.Combine(sourceDirectory, bmson ? "chart.bmson" : "chart.bms");
            string destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(sourcePath));
            File.WriteAllText(sourcePath, bmson
                ? "{\"version\":\"1.0.0\",\"info\":{\"title\":\"Merge\",\"mode_hint\":\"beat-7k\"},"
                    + "\"sound_channels\":[{\"name\":\"sound.wav\",\"notes\":[{\"x\":1,\"y\":0,\"l\":0}]}]}"
                : "#PLAYER 1\r\n#TITLE Merge\r\n#WAV01 sound.wav\r\n#00111:01\r\n");
            string resourcePath = Path.Combine(destinationDirectory, "sound.wav");
            File.WriteAllText(resourcePath, "destination resource");
            BMSFile? bmsFile = bmson ? null : BMSFile.CreateBMSFileFromFile(sourcePath);
            LR2SongDBExtended.bmson_song? bmsonSong = bmson ? BmsonSongParser.Parse(sourcePath) : null;
            ChartFile chart = bmson ? ChartFileProjection.FromBmsonSong(bmsonSong!) : ChartFileProjection.FromBmsFile(bmsFile!);
            BMSFileMaintenanceInfo initialInfo = BmsLibraryMaintenanceService.BuildResourceHealthMaintenanceInfo(chart);
            Assert.AreEqual(1, initialInfo.wav_files_defined);
            Assert.AreEqual(0, initialInfo.wav_files_existing);
            if (bmson)
            {
                bmsonSong!.MaintenanceInfo = initialInfo;
            }
            else
            {
                bmsFile!.SetMaintenanceInfo(initialInfo, suppressPropertyChanged: true);
            }
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                if (bmson)
                {
                    db.InsertOrReplace(bmsonSong!, typeof(LR2SongDBExtended.bmson_song));
                }
                else
                {
                    db.InsertOrReplace(bmsFile!.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                }
                db.InsertOrReplace(initialInfo, typeof(LR2SongDBExtended.maintenance));
                if (maintenanceOutcome == 1)
                {
                    // merge の既存情報の path 移転は通し、移動先 resource 再検査の書込だけを拒否します。
                    db.Execute("CREATE TRIGGER fail_merge_maintenance BEFORE INSERT ON maintenance "
                        + "WHEN NEW.path = '" + destinationPath.Replace("'", "''") + "' AND NEW.wav_files_existing = 1 "
                        + "BEGIN SELECT RAISE(ABORT, 'forced post-commit maintenance failure'); END;");
                }
            }
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BMSFiles = bmsFile == null ? [] : [bmsFile],
                BmsonSongs = bmsonSong == null ? [] : [bmsonSong]
            };
            ResourceHealthIndexSnapshot beforeSnapshot = library.GetResourceHealthIndexSnapshotForView("merge_health_before");
            Assert.AreEqual(1, beforeSnapshot.TargetCount);
            bool notifiedWithCurrentHealth = false;
            bool notifiedWhileReserved = false;
            LibraryFileMutationLease? competingReservation = null;
            bool competingReservationAcquired = false;
            library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    return;
                }
                using (LibraryFileMutationLease probe = library.TryBeginLibraryFileMutation("merge_health_notification_probe"))
                {
                    notifiedWhileReserved |= probe == null;
                }
                notifiedWithCurrentHealth |= (bmsonSong?.MaintenanceInfo ?? bmsFile?.TryGetMaintenanceInfoWithoutCreating())?.wav_files_existing == 1;
                if (maintenanceOutcome == 2 && competingReservation == null)
                {
                    // merge 公開後に別操作が受理される本番経路で、maintenance の予約再取得を拒否させます。
                    competingReservation = library.TryBeginLibraryFileMutation("competing_after_merge");
                    competingReservationAcquired = competingReservation != null;
                }
            };

            DuplicateMergeMaintenanceReceipt receipt;
            try
            {
                receipt = library.MergeChartDirectory(sourceDirectory, destinationDirectory, operationId: 1, reportAtTerminal: true);
            }
            finally
            {
                competingReservation?.Dispose();
            }

            Assert.IsTrue(receipt.MergeApplied);
            Assert.IsTrue(receipt.SessionReceipt.DurableCommit);
            Assert.AreEqual(new LibraryMutationSessionApplyCounts
            {
                CatalogApplyCount = 1,
                PackageReferenceApplyCount = 1,
                ReverseLookupApplyCount = 1,
                Lr2SyncCount = 1,
                RequiredPublicationCount = 1
            }, receipt.SessionReceipt.ApplyCounts);
            Assert.AreEqual(1, receipt.SessionReceipt.ConfirmedChangeCount);
            Assert.AreEqual(1, receipt.SessionReceipt.CatalogChartPathChangeCount);
            Assert.AreEqual(sourceDirectory, receipt.SessionReceipt.ConfirmedTargets.Single().SourcePath);
            Assert.AreEqual(destinationDirectory, receipt.SessionReceipt.ConfirmedTargets.Single().DestinationPath);
            Assert.IsFalse(notifiedWhileReserved);
            Assert.AreEqual(destinationPath, bmsonSong?.path ?? bmsFile!.path);
            Assert.IsFalse(Directory.Exists(sourceDirectory));
            Assert.IsTrue(File.Exists(destinationPath));
            Assert.AreEqual("destination resource", File.ReadAllText(resourcePath));
            using var readback = new LR2SongDBExtended(songDbPath);
            LR2SongDBExtended.maintenance persisted = readback.Table<LR2SongDBExtended.maintenance>().Single(row => row.path == destinationPath);
            Assert.AreEqual(1, persisted.wav_files_defined);
            Assert.AreEqual(0, readback.Table<LR2SongDBExtended.maintenance>().Count(row => row.path == sourcePath));
            using (LibraryFileMutationLease afterMerge = library.TryBeginLibraryFileMutation("merge_health_completion_probe"))
            {
                Assert.IsNotNull(afterMerge);
            }
            if (maintenanceOutcome != 0)
            {
                Assert.IsTrue(receipt.HasDurableFinalizationFailure);
                Assert.IsNotNull(receipt.SessionReceipt.FinalizationFailure);
                Assert.AreSame(receipt.SessionReceipt.FinalizationFailure, receipt.SessionReceipt.PrimaryFailure);
                Assert.IsFalse(receipt.MaintenanceHadUpdates);
                Assert.IsFalse(notifiedWithCurrentHealth);
                Assert.AreEqual(0, persisted.wav_files_existing);
                Assert.AreEqual(maintenanceOutcome == 2, receipt.MaintenanceResult.Canceled);
                Assert.AreEqual(maintenanceOutcome == 2, competingReservationAcquired);
                return;
            }
            Assert.IsFalse(receipt.SessionReceipt.HasRequiredFailure);
            Assert.IsTrue(receipt.MaintenanceHadUpdates);
            Assert.IsTrue(receipt.MaintenanceResult.CheckedFileCount > 0);
            Assert.AreEqual(ResourceHealthIndexUpdateMode.DeferOnUpdates, receipt.IntermediateMode);
            Assert.IsTrue(receipt.ResourceHealthIndexDeferred);
            Assert.IsFalse(receipt.ResourceHealthIndexDeltaApplied);
            Assert.IsFalse(receipt.ResourceHealthIndexFullRebuilt);
            Assert.IsTrue(notifiedWithCurrentHealth);
            ChartFile installed = bmson ? ChartFileProjection.FromBmsonSong(bmsonSong!) : ChartFileProjection.FromBmsFile(bmsFile!);
            Assert.IsFalse(new BmsLibraryMaintenanceService().BuildResourceHealthWarnings(installed)
                .Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.AreEqual(1, persisted.wav_files_existing);
            Assert.AreSame(ResourceHealthIndexSnapshot.Empty, library.TryGetCurrentResourceHealthIndexSnapshotForView());

            var table = new MainChartListViewModel();
            PlaylistWorkspaceViewModel workspace = RegularChartListOwnerTestSupport.CreateWorkspaceForOwner(table);
            using RegularChartListOwner owner = RegularChartListOwnerTestSupport.CreateOwner(table, workspace);
            var route = new ChartListRefreshRoute(
                ChartListRefreshRouteKind.ContinueMainLibrary,
                MainViewUpdateMode.FolderFilterSelected,
                MainViewUpdateMode.FolderFilterSelected,
                MainViewUpdateMode.FolderFilterSelected,
                isPlaylistTreeActive: false,
                includeBmsonRows: bmson);
            RegularChartListEntryResult firstView = owner.ApplyMainLibraryView(
                route, library, parameter: null, treeParameter: null, preserveSummary: false, Stopwatch.StartNew());

            Assert.IsTrue(firstView.WasCommitted);
            Assert.AreEqual(1, table.Rows.Count);
            ResourceHealthIndexSnapshot rebuiltSnapshot = library.TryGetCurrentResourceHealthIndexSnapshotForView();
            Assert.AreNotSame(ResourceHealthIndexSnapshot.Empty, rebuiltSnapshot);
            Assert.AreNotSame(beforeSnapshot, rebuiltSnapshot);
            Assert.AreEqual(1, rebuiltSnapshot.TargetCount);
            Assert.IsFalse(rebuiltSnapshot.GetProjection(installed.Kind, installed.Path, installed.Md5).HasIssues);
            var displayedRow = (LibraryChartRow)table.Rows[0]!;
            Assert.IsFalse(displayedRow.WarningDigestText.Contains(Resources.WarningDigest_ResourceMissing, StringComparison.Ordinal));
            Assert.IsFalse(displayedRow.WarningTooltipText.Contains("WAV", StringComparison.OrdinalIgnoreCase));

            RegularChartListEntryResult cachedView = owner.ApplyMainLibraryView(
                route, library, parameter: null, treeParameter: null, preserveSummary: false, Stopwatch.StartNew());
            Assert.IsTrue(cachedView.WasCommitted);
            Assert.AreSame(rebuiltSnapshot, library.TryGetCurrentResourceHealthIndexSnapshotForView());
        });
    }

    /// <summary>
    /// 読込み済みの複数exact source行を実mergeで処理し、確定した物理配置とcatalogを揃えます。
    /// </summary>
    [DataTestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, true, false)]
    [DataRow(false, false, true)]
    [DataRow(false, true, true)]
    [DataRow(true, false, false)]
    [DataRow(true, true, false)]
    [DataRow(true, false, true)]
    [DataRow(true, true, true)]
    public void MergeChartDirectory_ConsumesEveryConfirmedExactSourceKey(bool bmson, bool caseVariant, bool destinationExists)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string source = Path.Combine(root, "Source");
            string destination = Path.Combine(root, "Destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            string extension = bmson ? ".bmson" : ".bms";
            string firstPath = Path.Combine(source, "chart" + extension);
            string secondPath = Path.Combine(source, (caseVariant ? "CHART" : "other") + extension);
            string existingPath = Path.Combine(destination, "chart" + extension);
            string content = bmson ? "{\"version\":\"1.0.0\",\"info\":{\"title\":\"exact merge\",\"mode_hint\":\"beat-7k\"},\"sound_channels\":[]}"
                : "#PLAYER 1\r\n#TITLE exact merge\r\n#00111:01\r\n";
            File.WriteAllText(firstPath, content);
            if (!caseVariant)
            {
                File.WriteAllText(secondPath, content);
            }
            if (destinationExists)
            {
                File.WriteAllText(existingPath, content);
            }
            string[] inputPaths = destinationExists ? [firstPath, secondPath, existingPath] : [firstPath, secondPath];
            List<BMSFile> bmsRows = bmson ? [] : [.. inputPaths.Select(path => BMSFile.CreateBMSFileFromFile(path))];
            List<LR2SongDBExtended.bmson_song> bmsonRows = bmson ? [.. inputPaths.Select(path => BmsonSongParser.Parse(path))] : [];
            if (destinationExists && !bmson)
            {
                bmsRows.Last().favorite = 7;
                bmsRows.Last().tag = "destination-user-tag";
            }
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                foreach (BMSFile row in bmsRows)
                {
                    db.InsertOrReplace(row, typeof(LR2SongDB.song));
                }
                foreach (LR2SongDBExtended.bmson_song row in bmsonRows)
                {
                    db.InsertOrReplace(row, typeof(LR2SongDBExtended.bmson_song));
                }
                foreach (string path in inputPaths)
                {
                    db.InsertOrReplace(new BMSFileMaintenanceInfo { path = path }, typeof(LR2SongDBExtended.maintenance));
                }
            }
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BMSFiles = bmsRows,
                BmsonSongs = bmsonRows
            };

            string lookupHash = bmson ? bmsonRows[0].md5 : bmsRows[0].hash;
            string lookupSha256 = bmson ? bmsonRows[0].sha256 : bmsRows[0].sha256;
            LibraryChartKind lookupKind = bmson ? LibraryChartKind.Bmson : LibraryChartKind.Bms;
            OwnedChartHashIndexVersionedSnapshot beforeHash = library.GetOwnedChartHashIndexSnapshot();
            InstalledChartLookupIndexSnapshot beforeInstalled = library.CreateInstalledChartLookupSnapshotForDiagnostics();
            PlaylistLibraryResolveIndexSnapshot beforePlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool beforePlaylistCacheHit,
                out int beforePlaylistStaleRetries);
            Assert.IsFalse(beforePlaylistCacheHit);
            Assert.AreEqual(0, beforePlaylistStaleRetries);
            Assert.IsTrue(beforeHash.ContainsMd5(lookupHash));
            Assert.IsTrue(beforeInstalled.ContainsPrimaryHash(lookupHash));
            LibraryChartRef beforeRepresentative = beforePlaylist.ResolveChartForPlaylistHash(lookupHash, null);
            Assert.IsNotNull(beforeRepresentative);
            string beforeRepresentativePath = beforeRepresentative!.Path;
            string beforeRepresentativeMd5 = beforeRepresentative.Md5;
            string beforeRepresentativeSha256 = beforeRepresentative.Sha256;
            (LibraryChartKind Kind, string Path, string Md5, string Sha256)[] beforeCandidates =
                CapturePlaylistCandidateFacts(beforePlaylist.GetMd5Candidates(lookupHash));
            Assert.IsTrue(beforeCandidates.Length > 0);
            Assert.IsTrue(beforeCandidates.All(candidate =>
                candidate.Kind == lookupKind
                && string.Equals(candidate.Md5, lookupHash, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Sha256, lookupSha256, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(beforeCandidates.Any(candidate =>
                string.Equals(candidate.Path, firstPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Path, secondPath, StringComparison.OrdinalIgnoreCase)));

            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            DuplicateMergeMaintenanceReceipt receipt = library.MergeChartDirectory(source, destination, operationId: 1);

            Assert.IsTrue(receipt.MergeApplied, receipt.SessionReceipt.PrimaryFailure?.ToString());
            NormalLibraryRefreshNotificationBatch notificationBatch =
                library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            bool sourceCleanupExpected = destinationExists || caseVariant;
            Assert.AreEqual(sourceCleanupExpected, notificationBatch.NotifiesStorageRows);
            Assert.AreEqual(!bmson && sourceCleanupExpected, notificationBatch.NotifiesBmsFiles);
            Assert.AreEqual(bmson && sourceCleanupExpected, notificationBatch.NotifiesBmsonSongs);
            Assert.IsFalse(Directory.Exists(source));
            string[] currentPaths = Directory.GetFiles(destination, "*" + extension);
            Assert.IsTrue(currentPaths.Length > 0);
            foreach (string path in currentPaths)
            {
                Assert.AreEqual(content, File.ReadAllText(path));
            }
            CollectionAssert.AreEquivalent(currentPaths, bmson
                ? library.BmsonSongs.Select(row => row.path).ToArray()
                : library.BMSFiles.Select(row => row.path).ToArray());
            using var readback = new LR2SongDBExtended(songDbPath);
            CollectionAssert.AreEquivalent(currentPaths, bmson
                ? readback.Table<LR2SongDBExtended.bmson_song>().Select(row => row.path).ToArray()
                : readback.Table<LR2SongDB.song>().Select(row => row.path).ToArray());
            Assert.IsNull(readback.Find<LR2SongDBExtended.maintenance>(firstPath));
            Assert.IsNull(readback.Find<LR2SongDBExtended.maintenance>(secondPath));
            if (destinationExists && !bmson)
            {
                Assert.AreEqual(7, readback.Find<LR2SongDB.song>(existingPath).favorite);
                Assert.AreEqual("destination-user-tag", readback.Find<LR2SongDB.song>(existingPath).tag);
            }
            OwnedChartHashIndexVersionedSnapshot afterHash = library.GetOwnedChartHashIndexSnapshot();
            InstalledChartLookupIndexSnapshot afterInstalled = library.CreateInstalledChartLookupSnapshotForDiagnostics();
            PlaylistLibraryResolveIndexSnapshot afterPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool afterPlaylistCacheHit,
                out int afterPlaylistStaleRetries);
            Assert.IsTrue(afterPlaylistCacheHit);
            Assert.AreEqual(0, afterPlaylistStaleRetries);
            Assert.IsTrue(afterHash.ContainsMd5(lookupHash));
            Assert.IsTrue(afterInstalled.ContainsPrimaryHash(lookupHash));
            Assert.IsTrue(afterInstalled.GetDistinctDirectoriesByPrimaryHash(lookupHash)
                .Contains(destination, StringComparer.OrdinalIgnoreCase));
            Assert.IsFalse(afterInstalled.GetDistinctDirectoriesByPrimaryHash(lookupHash)
                .Contains(source, StringComparer.OrdinalIgnoreCase));
            LibraryChartRef afterRepresentative = afterPlaylist.ResolveChartForPlaylistHash(lookupHash, null);
            Assert.IsNotNull(afterRepresentative);
            Assert.IsNotNull(beforePlaylist.ResolveChartForPlaylistHash(lookupHash, null));
            (LibraryChartKind Kind, string Path, string Md5, string Sha256)[] afterCandidates =
                CapturePlaylistCandidateFacts(afterPlaylist.GetMd5Candidates(lookupHash));
            Assert.IsTrue(afterCandidates.Length > 0);
            Assert.IsTrue(afterCandidates.All(candidate =>
                candidate.Kind == lookupKind
                && string.Equals(candidate.Md5, lookupHash, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Sha256, lookupSha256, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(afterCandidates.All(candidate =>
                currentPaths.Contains(candidate.Path, StringComparer.OrdinalIgnoreCase)));
            foreach (string oldPath in new[] { firstPath, secondPath })
            {
                Assert.IsFalse(afterPlaylist.ContainsCandidate(lookupKind, oldPath));
            }
            foreach (string currentPath in currentPaths)
            {
                Assert.IsTrue(afterPlaylist.ContainsCandidate(lookupKind, currentPath));
            }
            Assert.AreEqual(beforeRepresentativePath, beforePlaylist.ResolveChartForPlaylistHash(lookupHash, null)!.Path);
            Assert.AreEqual(beforeRepresentativeMd5, beforePlaylist.ResolveChartForPlaylistHash(lookupHash, null)!.Md5);
            Assert.AreEqual(beforeRepresentativeSha256, beforePlaylist.ResolveChartForPlaylistHash(lookupHash, null)!.Sha256);
            CollectionAssert.AreEqual(
                beforeCandidates,
                CapturePlaylistCandidateFacts(beforePlaylist.GetMd5Candidates(lookupHash)));
        });
    }

    /// <summary>
    /// BMSの重複源cleanupとBMSONのpath-only移動を同じ実mergeで処理し、
    /// cleanupされた種類だけstorage row通知を発行します。
    /// </summary>
    [TestMethod]
    public void MergeChartDirectory_MixedKindsNotifiesOnlyRemovedStorageRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string source = Path.Combine(root, "Source");
            string destination = Path.Combine(root, "Destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);

            string sourceBmsPath = Path.Combine(source, "duplicate.bms");
            string destinationBmsPath = Path.Combine(destination, "duplicate.bms");
            string sourceBmsonPath = Path.Combine(source, "moved.bmson");
            string destinationBmsonPath = Path.Combine(destination, "moved.bmson");
            string bmsContent = "#PLAYER 1\r\n#TITLE mixed duplicate\r\n#00111:01\r\n";
            string bmsonContent =
                "{\"version\":\"1.0.0\",\"info\":{\"title\":\"mixed moved\",\"mode_hint\":\"beat-7k\"},\"sound_channels\":[]}";
            File.WriteAllText(sourceBmsPath, bmsContent);
            File.WriteAllText(destinationBmsPath, bmsContent);
            File.WriteAllText(sourceBmsonPath, bmsonContent);

            var sourceBms = BMSFile.CreateBMSFileFromFile(sourceBmsPath);
            var destinationBms = BMSFile.CreateBMSFileFromFile(destinationBmsPath);
            LR2SongDBExtended.bmson_song sourceBmson = BmsonSongParser.Parse(sourceBmsonPath);
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.InsertOrReplace(sourceBms, typeof(LR2SongDB.song));
                setup.InsertOrReplace(destinationBms, typeof(LR2SongDB.song));
                setup.InsertOrReplace(sourceBmson, typeof(LR2SongDBExtended.bmson_song));
                setup.InsertOrReplace(new BMSFileMaintenanceInfo { path = sourceBmsPath }, typeof(LR2SongDBExtended.maintenance));
                setup.InsertOrReplace(new BMSFileMaintenanceInfo { path = destinationBmsPath }, typeof(LR2SongDBExtended.maintenance));
                setup.InsertOrReplace(new BMSFileMaintenanceInfo { path = sourceBmsonPath }, typeof(LR2SongDBExtended.maintenance));
            }

            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new TestFileMutationService(),
                new RecordingDialogService())
            {
                BMSFiles = [sourceBms, destinationBms],
                BmsonSongs = [sourceBmson]
            };

            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            DuplicateMergeMaintenanceReceipt receipt = library.MergeChartDirectory(source, destination, operationId: 1);

            Assert.IsTrue(receipt.MergeApplied, receipt.SessionReceipt.PrimaryFailure?.ToString());
            NormalLibraryRefreshNotificationBatch notificationBatch =
                library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            Assert.IsTrue(notificationBatch.NotifiesStorageRows);
            Assert.IsTrue(notificationBatch.NotifiesBmsFiles);
            Assert.IsFalse(notificationBatch.NotifiesBmsonSongs);
            Assert.IsFalse(Directory.Exists(source));
            Assert.IsTrue(File.Exists(destinationBmsPath));
            Assert.IsTrue(File.Exists(destinationBmsonPath));
            CollectionAssert.AreEquivalent(
                new[] { destinationBmsPath },
                library.BMSFiles.Select(file => file.path).ToArray());
            CollectionAssert.AreEquivalent(
                new[] { destinationBmsonPath },
                library.BmsonSongs.Select(song => song.path).ToArray());
            using var readback = new LR2SongDBExtended(songDbPath);
            CollectionAssert.AreEquivalent(
                new[] { destinationBmsPath },
                readback.Table<LR2SongDB.song>().Select(row => row.path).ToArray());
            CollectionAssert.AreEquivalent(
                new[] { destinationBmsonPath },
                readback.Table<LR2SongDBExtended.bmson_song>().Select(row => row.path).ToArray());
            Assert.IsNull(readback.Find<LR2SongDBExtended.maintenance>(sourceBmsPath));
            Assert.IsNull(readback.Find<LR2SongDBExtended.maintenance>(sourceBmsonPath));
        });
    }

    /// <summary>
    /// スキャンを省略した通常起動ではmergeを拒否し、file diff収束後だけ同じ操作を受理します。
    /// </summary>
    [DataTestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, true, false)]
    [DataRow(false, true, true)]
    [DataRow(true, false, false)]
    [DataRow(true, true, false)]
    [DataRow(true, true, true)]
    public void MergeChartDirectory_AfterStartupWithoutFileScanRejectsUntilReloadFileDiffConverges(
        bool bmson, bool includePlainRow, bool dotFirst)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string source = Path.Combine(root, "Source");
            string destination = Path.Combine(root, "Destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            string extension = bmson ? ".bmson" : ".bms";
            string plainPath = Path.Combine(source, "chart" + extension);
            string dotPath = Path.Combine(source, ".", "chart" + extension);
            string sourceSharedPath = Path.Combine(source, "shared" + extension);
            string destinationSharedPath = Path.Combine(destination, "shared" + extension);
            string destinationChartPath = Path.Combine(destination, "chart" + extension);
            string content = bmson
                ? "{\"version\":\"1.0.0\",\"info\":{\"title\":\"unique chart\",\"mode_hint\":\"beat-7k\"},\"sound_channels\":[]}"
                : "#PLAYER 1\r\n#TITLE unique chart\r\n#00111:01\r\n";
            string sharedContent = content.Replace("unique chart", "shared chart", StringComparison.Ordinal);
            File.WriteAllText(plainPath, content);
            File.WriteAllText(sourceSharedPath, sharedContent);
            File.WriteAllText(destinationSharedPath, sharedContent);
            string[] oldPaths = !includePlainRow ? [dotPath]
                : dotFirst ? [dotPath, plainPath] : [plainPath, dotPath];
            string[] inputPaths = [.. oldPaths, sourceSharedPath, destinationSharedPath];
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                foreach (string path in inputPaths)
                {
                    // parserのI/O正規化とは分けて、起動前に存在する旧DBのraw keyを保存する。
                    if (bmson)
                    {
                        LR2SongDBExtended.bmson_song row = BmsonSongParser.Parse(path);
                        row.path = path;
                        setup.InsertOrReplace(row, typeof(LR2SongDBExtended.bmson_song));
                    }
                    else
                    {
                        var row = BMSFile.CreateBMSFileFromFile(path);
                        row.path = path;
                        setup.InsertOrReplace(row, typeof(LR2SongDB.song));
                    }
                    setup.InsertOrReplace(new BMSFileMaintenanceInfo { path = path }, typeof(LR2SongDBExtended.maintenance));
                }
            }
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = false,
                ScanBmsFilesOnStartup = false,
                PendingInstallEstimateMaxParallelPackages = 1
            };
            IChartFileScanner chartFileScanner = CapturedChartFileScanner.FromFixture(
                [plainPath, sourceSharedPath, destinationSharedPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [source] = [],
                    [destination] = []
                },
                [root]);
            var library = new TestBmsLibrary(songDbPath, null, null,
                new TestFileMutationService(), new RecordingDialogService(),
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher), () => options,
                chartFileScanner)
            {
                SearchTargets = [root],
                StartupBackgroundTaskScheduler = (_, _, _, _) => false
            };
            try
            {
                library.InitializeStartup(null);
                CollectionAssert.AreEquivalent(inputPaths, bmson
                    ? library.BmsonSongs.Select(row => row.path).ToArray()
                    : library.BMSFiles.Select(row => row.path).ToArray());
                library.SearchDuplicateChartGroups();
                Assert.IsTrue(library.DuplicateChartGroups.Any(group =>
                    group.Folders.Contains(source) && group.Folders.Contains(destination)));

                DuplicateMergeMaintenanceReceipt blockedReceipt = library.MergeChartDirectory(source, destination, operationId: 1);

                Assert.IsFalse(blockedReceipt.MergeApplied);
                Assert.IsTrue(Directory.Exists(source));
                CollectionAssert.AreEquivalent(
                    new[] { plainPath, sourceSharedPath },
                    Directory.GetFiles(source, "*" + extension, System.IO.SearchOption.AllDirectories));
                CollectionAssert.AreEquivalent(
                    new[] { destinationSharedPath },
                    Directory.GetFiles(destination, "*" + extension, System.IO.SearchOption.AllDirectories));
                CollectionAssert.AreEquivalent(inputPaths, bmson
                    ? library.BmsonSongs.Select(row => row.path).ToArray()
                    : library.BMSFiles.Select(row => row.path).ToArray());
                using (var blockedReadback = new LR2SongDBExtended(songDbPath))
                {
                    CollectionAssert.AreEquivalent(inputPaths, bmson
                        ? blockedReadback.Table<LR2SongDBExtended.bmson_song>().Select(row => row.path).ToArray()
                        : blockedReadback.Table<LR2SongDB.song>().Select(row => row.path).ToArray());
                    foreach (string path in inputPaths)
                    {
                        Assert.IsNotNull(blockedReadback.Find<LR2SongDBExtended.maintenance>(path));
                    }
                }

                library.ReloadFileDiff();
                string[] convergedPaths = [plainPath, sourceSharedPath, destinationSharedPath];
                CollectionAssert.AreEquivalent(convergedPaths, bmson
                    ? library.BmsonSongs.Select(row => row.path).ToArray()
                    : library.BMSFiles.Select(row => row.path).ToArray());
                library.SearchDuplicateChartGroups();
                Assert.IsTrue(library.DuplicateChartGroups.Any(group =>
                    group.Folders.Contains(source) && group.Folders.Contains(destination)));

                DuplicateMergeMaintenanceReceipt receipt = library.MergeChartDirectory(source, destination, operationId: 2);

                Assert.IsTrue(receipt.MergeApplied, receipt.SessionReceipt.PrimaryFailure?.ToString());
                Assert.IsFalse(Directory.Exists(source));
                string[] expectedPaths = [destinationChartPath, destinationSharedPath];
                CollectionAssert.AreEquivalent(expectedPaths, Directory.GetFiles(destination, "*" + extension, System.IO.SearchOption.AllDirectories));
                Assert.AreEqual(content, File.ReadAllText(destinationChartPath));
                Assert.AreEqual(sharedContent, File.ReadAllText(destinationSharedPath));
                CollectionAssert.AreEquivalent(expectedPaths, bmson
                    ? library.BmsonSongs.Select(row => row.path).ToArray()
                    : library.BMSFiles.Select(row => row.path).ToArray());
                using var readback = new LR2SongDBExtended(songDbPath);
                CollectionAssert.AreEquivalent(expectedPaths, bmson
                    ? readback.Table<LR2SongDBExtended.bmson_song>().Select(row => row.path).ToArray()
                    : readback.Table<LR2SongDB.song>().Select(row => row.path).ToArray());
                foreach (string oldPath in oldPaths.Append(sourceSharedPath))
                {
                    Assert.IsNull(readback.Find<LR2SongDBExtended.maintenance>(oldPath));
                }
            }
            finally
            {
                library.RequestShutdown("dot-alias-merge-test");
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
                TestFileMutationService fileMutationService = new();
                var library = new TestBmsLibrary(songDbPath, null, null, fileMutationService, new RecordingDialogService());
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
                bool sourceCleanupObserved = false;
                fileMutationService.AfterDeleteDirectory = deletedDirectoryPath =>
                {
                    if (!string.Equals(deletedDirectoryPath, srcDir, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    sourceCleanupObserved = true;
                    // The source collection is deliberately changed after the
                    // executor cleanup callback.  The merge maintenance input
                    // must already be captured, so this cannot erase its target.
                    library.BmsonSongs = [];
                };

                DuplicateMergeMaintenanceReceipt receipt = library.MergeChartDirectory(srcDir, dstDir, operationId: 1);

                Assert.IsTrue(receipt.MergeApplied);
                Assert.AreEqual(ResourceHealthIndexUpdateMode.DeferOnUpdates, receipt.IntermediateMode);
                Assert.IsTrue(sourceCleanupObserved);
                Assert.IsTrue(receipt.MaintenanceHadUpdates);
                Assert.IsTrue(receipt.MaintenanceResult.HasUpdates);
                Assert.IsTrue(receipt.MaintenanceResult.CheckedFileCount > 0);
                Assert.IsTrue(receipt.IntermediateDeferred);
                Assert.IsTrue(receipt.ResourceHealthIndexDeferred);
                Assert.IsTrue(receipt.MaintenanceResult.ResourceHealthIndexDeferred);
                Assert.IsFalse(receipt.ResourceHealthIndexDeltaApplied);
                Assert.IsFalse(receipt.MaintenanceResult.ResourceHealthIndexDeltaApplied);
                Assert.IsFalse(receipt.ResourceHealthIndexFullRebuilt);
                Assert.IsFalse(receipt.MaintenanceResult.ResourceHealthIndexFullRebuilt);
                Assert.AreSame(ResourceHealthIndexSnapshot.Empty, library.TryGetCurrentResourceHealthIndexSnapshotForView());
                Assert.IsNotNull(receipt.SessionReceipt);

                string dstChartPath = Path.Combine(dstDir, "chart.bmson");
                Assert.IsFalse(File.Exists(srcChartPath));
                Assert.IsFalse(Directory.Exists(srcDir));
                Assert.IsTrue(File.Exists(dstChartPath));
                // Restore the fixture state changed by the cleanup seam before
                // checking the normal post-merge owned collection contract.
                library.BmsonSongs = [sourceSong];
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

    /// <summary>
    /// canonical DB 書込に失敗しても、確認済み physical change と旧登録を失わず、
    /// source cleanup や filesystem rollback を行わずに復旧候補を session へ残します。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MergeChartDirectory_CatalogFailureRetainsPreparedFilesAndSessionFacts(bool bmson)
    {
        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string source = Path.Combine(root, "Source");
            string destination = Path.Combine(root, "Destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            string sourcePath = Path.Combine(source, bmson ? "chart.bmson" : "chart.bms");
            string destinationPath = Path.Combine(destination, Path.GetFileName(sourcePath));
            File.WriteAllText(sourcePath, bmson
                ? "{\"version\":\"1.0.0\",\"info\":{\"title\":\"Merge\"}}"
                : "#PLAYER 1\r\n#TITLE Merge\r\n#00111:01\r\n");
            byte[] sourceBytes = File.ReadAllBytes(sourcePath);
            BMSFile? bmsFile = bmson ? null : BMSFile.CreateBMSFileFromFile(sourcePath);
            LR2SongDBExtended.bmson_song? bmsonSong = bmson ? BmsonSongParser.Parse(sourcePath) : null;
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                if (bmson)
                {
                    db.InsertOrReplace(bmsonSong!, typeof(LR2SongDBExtended.bmson_song));
                }
                else
                {
                    db.InsertOrReplace(bmsFile!.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                }
                db.Execute("CREATE TRIGGER fail_merge_catalog BEFORE INSERT ON " + (bmson ? "bmson_song" : "song")
                    + " WHEN NEW.path = '" + destinationPath.Replace("'", "''") + "' "
                    + "BEGIN SELECT RAISE(ABORT, 'forced canonical merge failure'); END;");
            }
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BMSFiles = bmsFile == null ? [] : [bmsFile],
                BmsonSongs = bmsonSong == null ? [] : [bmsonSong]
            };
            int ownedCollectionPublicationCount = 0;
            library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(BMSLibrary.OwnedChartCollectionVersion))
                {
                    ownedCollectionPublicationCount++;
                }
            };

            DuplicateMergeMaintenanceReceipt receipt = library.MergeChartDirectory(source, destination, operationId: 1, reportAtTerminal: true);

            Assert.IsFalse(receipt.MergeApplied);
            Assert.IsFalse(receipt.HasDurableCommit);
            Assert.IsNotNull(receipt.SessionReceipt.ApplyFailure);
            Assert.IsNull(receipt.SessionReceipt.PhysicalFailure);
            Assert.AreEqual(1, receipt.SessionReceipt.ConfirmedChangeCount);
            Assert.AreEqual(1, receipt.SessionReceipt.CatalogChartPathChangeCount);
            Assert.AreEqual(sourcePath, bmsonSong?.path ?? bmsFile!.path);
            CollectionAssert.AreEqual(sourceBytes, File.ReadAllBytes(sourcePath));
            CollectionAssert.AreEqual(sourceBytes, File.ReadAllBytes(destinationPath));
            Assert.IsTrue(receipt.RecoveryPaths.Contains(sourcePath, StringComparer.OrdinalIgnoreCase));
            Assert.IsTrue(receipt.RecoveryPaths.Contains(destinationPath, StringComparer.OrdinalIgnoreCase));
            Assert.IsFalse(receipt.MaintenanceHadUpdates);
            Assert.AreEqual(0, ownedCollectionPublicationCount);
            using var readback = new LR2SongDBExtended(songDbPath);
            if (bmson)
            {
                Assert.IsNotNull(readback.Find<LR2SongDBExtended.bmson_song>(sourcePath));
                Assert.IsNull(readback.Find<LR2SongDBExtended.bmson_song>(destinationPath));
            }
            else
            {
                Assert.IsNotNull(readback.Find<LR2SongDB.song>(sourcePath));
                Assert.IsNull(readback.Find<LR2SongDB.song>(destinationPath));
            }
        });
    }

    /// <summary>
    /// LR2 必須反映の失敗後も durable facts と source cleanup を維持し、成功公開と maintenance を行いません。
    /// </summary>
    [TestMethod]
    public void MergeChartDirectory_Lr2FinalizationFailureReturnsDurableNonSuccessWithoutMaintenancePublication()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(
                Path.GetTempPath(),
                "BeMusicSeeker_DuplicateMergeDurableFinalizationFailure_" + Guid.NewGuid().ToString("N"));
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Source");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "PackFinalizationFailure");
            string sourceChartPath = Path.Combine(sourceDirectoryPath, "chart.bms");
            string destinationChartPath = Path.Combine(destinationDirectoryPath, "chart.bms");
            string lr2RootPath = Path.Combine(tempRootPath, "LR2beta3");
            Directory.CreateDirectory(sourceDirectoryPath);
            Directory.CreateDirectory(destinationDirectoryPath);
            File.WriteAllText(
                sourceChartPath,
                "#PLAYER 1\r\n#TITLE merge finalization failure\r\n#ARTIST artist\r\n#00111:01\r\n",
                Encoding.ASCII);
            try
            {
                var sourceFile = BMSFile.CreateBMSFileFromFile(sourceChartPath);
                LR2Config lr2Config = BmsPlaylistTestSupport.CreateLr2Config(lr2RootPath, tempRootPath);
                var library = new TestBmsLibrary(
                    songDbPath,
                    () => lr2Config,
                    null,
                    new TestFileMutationService(),
                    new RecordingDialogService(),
                    new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    () => new BmsLibraryOptionsSnapshot
                    {
                        OperationModeLR2DB = true,
                        LR2RootPath = lr2RootPath
                    })
                {
                    BMSFiles = [sourceFile],
                    BmsonSongs = []
                };
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(sourceFile.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                    songDb.Execute(
                        "CREATE TRIGGER fail_lr2_folder_insert BEFORE INSERT ON folder WHEN NEW.path LIKE '%PackFinalizationFailure%' "
                        + "BEGIN SELECT RAISE(ABORT, 'forced durable finalization failure'); END;");
                }

                int ownedCollectionPublicationCount = 0;
                int normalRefreshPublicationCount = 0;
                library.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(BMSLibrary.OwnedChartCollectionVersion))
                    {
                        ownedCollectionPublicationCount++;
                    }
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        normalRefreshPublicationCount++;
                    }
                };

                DuplicateMergeMaintenanceReceipt receipt = library.MergeChartDirectory(
                    sourceDirectoryPath,
                    destinationDirectoryPath,
                    operationId: 1);

                Assert.IsNotNull(receipt);
                Assert.IsFalse(receipt.MergeApplied);
                Assert.IsTrue(receipt.HasDurableCommit);
                Assert.IsTrue(receipt.HasDurableFinalizationFailure);
                Assert.IsFalse(receipt.ManualRecoveryRequired);
                Assert.IsFalse(receipt.CompletedWithCleanupFailure);
                Assert.IsNotNull(receipt.SessionReceipt);
                Assert.IsTrue(receipt.SessionReceipt.HasDurableFinalizationFailure);
                Assert.IsTrue(receipt.SessionReceipt.DurableCommit);
                Assert.AreEqual(1, receipt.SessionReceipt.ConfirmedChangeCount);
                Assert.IsNotNull(receipt.SessionReceipt.ApplyFailure);
                Assert.AreSame(receipt.SessionReceipt.ApplyFailure, receipt.SessionReceipt.PrimaryFailure);
                Assert.IsFalse(receipt.MaintenanceHadUpdates);
                Assert.IsFalse(receipt.MaintenanceResult.HasUpdates);
                Assert.AreEqual(0, ownedCollectionPublicationCount);
                Assert.AreEqual(0, normalRefreshPublicationCount);
                Assert.IsFalse(File.Exists(sourceChartPath));
                Assert.IsTrue(File.Exists(destinationChartPath));
                using var verifySongDb = new LR2SongDBExtended(songDbPath);
                Assert.IsNull(verifySongDb.Find<LR2SongDB.song>(sourceChartPath));
                Assert.IsNotNull(verifySongDb.Find<LR2SongDB.song>(destinationChartPath));
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
    public void MergeChartDirectory_FailedDialogEffectIsIsolatedAndCanReenterAfterLeaseRelease()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_DuplicateMergeEffectFailure_" + System.Guid.NewGuid().ToString("N"));
            string srcDir = Path.Combine(tempRootPath, "Src");
            string dstDir = Path.Combine(tempRootPath, "Dst");
            string srcChartPath = Path.Combine(srcDir, "chart.bmson");
            string reentrySrcDir = Path.Combine(tempRootPath, "ReentrySrc");
            string reentryDstDir = Path.Combine(tempRootPath, "ReentryDst");
            string reentryChartPath = Path.Combine(reentrySrcDir, "reentry.bmson");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(dstDir);
            Directory.CreateDirectory(reentrySrcDir);
            Directory.CreateDirectory(reentryDstDir);
            File.WriteAllText(srcChartPath, "{}");
            File.WriteAllText(reentryChartPath, "{}");
            try
            {
                TestFileMutationService fileMutationService = new()
                {
                    FailNextMutationCount = 1
                };
                RecordingDialogService dialogService = new()
                {
                    ThrowOnShow = true
                };
                var library = new TestBmsLibrary(songDbPath, null, null, fileMutationService, dialogService);
                var sourceSong = new LR2SongDBExtended.bmson_song
                {
                    path = srcChartPath,
                    folder = srcDir,
                    title = "failed merge target",
                    md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                };
                var reentrySong = new LR2SongDBExtended.bmson_song
                {
                    path = reentryChartPath,
                    folder = reentrySrcDir,
                    title = "reentry target",
                    md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                };
                library.BmsonSongs = [sourceSong, reentrySong];
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(sourceSong, typeof(LR2SongDBExtended.bmson_song));
                    songDb.InsertOrReplace(reentrySong, typeof(LR2SongDBExtended.bmson_song));
                }

                bool effectObserved = false;
                bool locksReleased = false;
                bool reentryCompleted = false;
                DuplicateMergeMaintenanceReceipt? reentryReceipt = null;
                dialogService.OnShow = () =>
                {
                    if (effectObserved)
                    {
                        return;
                    }

                    effectObserved = true;
                    locksReleased = !library.IsWriteLockHeldInitializeAll
                        && !library.IsWriteLockHeldInitializeMin
                        && !library.IsWriteLockHeldInitializeBMSFiles
                        && !library.IsWriteLockHeldPendingInstallCharts
                        && !library.IsWriteLockHeldDuplicateChartGroups;
                    reentryReceipt = library.MergeChartDirectory(reentrySrcDir, reentryDstDir, operationId: 2);
                    reentryCompleted = true;
                };

                DuplicateMergeMaintenanceReceipt receipt = library.MergeChartDirectory(srcDir, dstDir, operationId: 1);

                Assert.IsTrue(effectObserved);
                Assert.IsTrue(locksReleased);
                Assert.IsTrue(reentryCompleted);
                Assert.IsNotNull(reentryReceipt);
                DuplicateMergeMaintenanceReceipt completedReentryReceipt = reentryReceipt!;
                Assert.IsTrue(completedReentryReceipt.MergeApplied);
                Assert.AreEqual(1, dialogService.ShowCount);
                Assert.IsFalse(receipt.MergeApplied);
                Assert.IsNotNull(receipt.SessionReceipt);
                Assert.IsFalse(receipt.SessionReceipt.DurableCommit);
                Assert.AreEqual(0, receipt.SessionReceipt.ConfirmedChangeCount);
                Assert.IsNotNull(receipt.SessionReceipt.PhysicalFailure);
                Assert.IsTrue(File.Exists(srcChartPath));
                Assert.IsFalse(File.Exists(Path.Combine(dstDir, "chart.bmson")));
                using var verify = new LR2SongDBExtended(songDbPath);
                Assert.IsNotNull(verify.Find<LR2SongDBExtended.bmson_song>(srcChartPath));
                Assert.IsNull(verify.Find<LR2SongDBExtended.bmson_song>(Path.Combine(dstDir, "chart.bmson")));
                Assert.IsNotNull(verify.Find<LR2SongDBExtended.bmson_song>(Path.Combine(reentryDstDir, "reentry.bmson")));
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
                var sourceFile = BMSFile.CreateBMSFileFromFile(srcChartPath);
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

    /// <summary>
    /// 内容が異なる同名ファイルの衝突では、既存ファイル・登録を保ったまま、
    /// 移動した譜面の全 projection に採番後の実 destination を使います。
    /// </summary>
    [TestMethod]
    public void MergeChartDirectory_DifferentContentCollisionUsesActualDestinationEverywhere()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(
                Path.GetTempPath(),
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
            try
            {
                var sourceFile = BMSFile.CreateBMSFileFromFile(srcChartPath);
                var existingFile = BMSFile.CreateBMSFileFromFile(collisionPath);
                existingFile.favorite = 7;
                existingFile.adddate = 12345;
                existingFile.tag = "existing-user-tag";
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(sourceFile, typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(existingFile, typeof(LR2SongDB.song));
                }

                var library = new TestBmsLibrary(
                    songDbPath,
                    null,
                    null,
                    new TestFileMutationService(),
                    new RecordingDialogService())
                {
                    BMSFiles = [sourceFile, existingFile],
                    BmsonSongs = []
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

                Assert.AreEqual(actualDestinationPath, sourceFile.path);
                Assert.IsFalse(File.Exists(srcChartPath));
                Assert.IsTrue(File.Exists(collisionPath));
                Assert.IsTrue(File.Exists(actualDestinationPath));
                CollectionAssert.AreEqual(collisionBytes, File.ReadAllBytes(collisionPath));
                CollectionAssert.AreEqual(sourceBytes, File.ReadAllBytes(actualDestinationPath));
                Assert.IsNotNull(library.BMSFiles.SingleOrDefault(file =>
                    string.Equals(file.path, collisionPath, StringComparison.Ordinal)));
                Assert.IsNotNull(library.BMSFiles.SingleOrDefault(file =>
                    string.Equals(file.path, actualDestinationPath, StringComparison.Ordinal)));

                using var verify = new LR2SongDBExtended(songDbPath);
                LR2SongDB.song existingRow = verify.Find<LR2SongDB.song>(collisionPath);
                Assert.IsNotNull(existingRow);
                Assert.AreEqual(existingFile.hash, existingRow.hash);
                Assert.AreEqual(7, existingRow.favorite);
                Assert.AreEqual(12345, existingRow.adddate);
                Assert.AreEqual("existing-user-tag", existingRow.tag);
                LR2SongDB.song movedRow = verify.Find<LR2SongDB.song>(actualDestinationPath);
                Assert.IsNotNull(movedRow);
                Assert.AreEqual(sourceFile.hash, movedRow.hash);
                Assert.IsNull(verify.Find<LR2SongDB.song>(srcChartPath));
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

    /// <summary>
    /// 選択した source folder の merge は、同梱通常ファイルと既存ディレクトリの
    /// 型衝突が一件でもあれば detached package 全体を変更前に拒否し、source
    /// 登録、宛先、song.db を保持します。
    /// </summary>
    [TestMethod]
    public void MergeChartDirectory_RejectsTypeConflictAndPreservesSourceRegistration()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(
                Path.GetTempPath(),
                "BeMusicSeeker_DuplicateMergeTypeConflict_" + Guid.NewGuid().ToString("N"));
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Source");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Destination");
            string sourceChartPath = Path.Combine(sourceDirectoryPath, "chart.bms");
            string sourceBundledFilePath = Path.Combine(sourceDirectoryPath, "BGA");
            string destinationBundledDirectoryPath = Path.Combine(destinationDirectoryPath, "BGA");
            string destinationSentinelPath = Path.Combine(destinationBundledDirectoryPath, "sentinel.txt");
            Directory.CreateDirectory(sourceDirectoryPath);
            Directory.CreateDirectory(destinationBundledDirectoryPath);
            File.WriteAllText(sourceChartPath, "#PLAYER 1\r\n#TITLE merge conflict\r\n#ARTIST source\r\n#00111:01\r\n");
            File.WriteAllText(sourceBundledFilePath, "source bundled file");
            File.WriteAllText(destinationSentinelPath, "destination sentinel");
            byte[] sourceChartBytes = File.ReadAllBytes(sourceChartPath);
            byte[] sourceBundledFileBytes = File.ReadAllBytes(sourceBundledFilePath);
            try
            {
                var sourceFile = BMSFile.CreateBMSFileFromFile(sourceChartPath);
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(sourceFile.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                }

                var library = new TestBmsLibrary(
                    songDbPath,
                    null,
                    null,
                    new TestFileMutationService(),
                    new RecordingDialogService())
                {
                    BMSFiles = [sourceFile],
                    BmsonSongs = []
                };

                DuplicateMergeMaintenanceReceipt receipt = library.MergeChartDirectory(
                    sourceDirectoryPath,
                    destinationDirectoryPath,
                    operationId: 1);

                Assert.IsFalse(receipt.MergeApplied);
                Assert.IsNotNull(receipt.SessionReceipt);
                Assert.IsFalse(receipt.SessionReceipt.DurableCommit);
                Assert.AreEqual(0, receipt.SessionReceipt.ConfirmedChangeCount);
                Assert.AreEqual(1, receipt.SessionReceipt.ItemFailures.Count);
                Assert.IsFalse(receipt.SessionReceipt.HasRequiredFailure);
                FileDbMutationDestinationTypeConflict conflict = receipt.DestinationTypeConflicts.Single();
                Assert.AreEqual(sourceBundledFilePath, conflict.SourcePath);
                Assert.AreEqual(destinationBundledDirectoryPath, conflict.DestinationPath);
                Assert.IsFalse(conflict.ExpectedIsDirectory);
                Assert.IsTrue(conflict.ExistingIsDirectory);
                Assert.IsTrue(Directory.Exists(sourceDirectoryPath));
                CollectionAssert.AreEqual(sourceChartBytes, File.ReadAllBytes(sourceChartPath));
                CollectionAssert.AreEqual(sourceBundledFileBytes, File.ReadAllBytes(sourceBundledFilePath));
                Assert.IsTrue(Directory.Exists(destinationDirectoryPath));
                Assert.IsTrue(Directory.Exists(destinationBundledDirectoryPath));
                Assert.AreEqual("destination sentinel", File.ReadAllText(destinationSentinelPath));
                Assert.AreEqual(1, library.BMSFiles.Count);
                Assert.AreEqual(sourceChartPath, library.BMSFiles.Single().path);
                using var verifySongDb = new LR2SongDBExtended(songDbPath);
                Assert.IsNotNull(verifySongDb.Find<LR2SongDB.song>(sourceChartPath));
                Assert.IsNull(verifySongDb.Find<LR2SongDB.song>(Path.Combine(destinationDirectoryPath, "chart.bms")));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(LongPathFileSystem.ToExtendedPath(tempRootPath), recursive: true);
                }
            }
        });
    }

    /// <summary>
    /// 全譜面が重複でも resource-only merge を一つの session として確定し、移動先の登録を維持します。
    /// </summary>
    [TestMethod]
    public void MergeChartDirectory_BmsonDuplicateSkip_CommitsResourceOnlySession()
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
            File.WriteAllText(Path.Combine(srcDir, "sound.wav"), "source resource");
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

                DuplicateMergeMaintenanceReceipt receipt = library.MergeChartDirectory(srcDir, dstDir, operationId: 1);

                Assert.IsTrue(receipt.MergeApplied, receipt.SessionReceipt.PrimaryFailure?.ToString());
                Assert.IsTrue(receipt.SessionReceipt.DurableCommit);
                Assert.AreEqual(1, receipt.SessionReceipt.ConfirmedChangeCount);
                Assert.AreEqual(0, receipt.SessionReceipt.CatalogChartPathChangeCount);
                Assert.AreEqual(1, receipt.SessionReceipt.CatalogChartRemovalCount);
                Assert.AreEqual("source resource", File.ReadAllText(Path.Combine(dstDir, "sound.wav")));
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

    /// <summary>
    /// source に owned chart がない merge は、source確認だけで終了し、
    /// cold/warm いずれも optional lookup を先行構築しない。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MergeChartDirectory_NoSourceChartsDoesNotBuildLookups(bool sourceExists)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string source = Path.Combine(root, "EmptySource");
            string destination = Path.Combine(root, "EmptyDestination");
            if (sourceExists)
            {
                Directory.CreateDirectory(source);
            }
            Directory.CreateDirectory(destination);
            List<TestableBmsFile> files = [];
            for (int index = 0; index < 16; index++)
            {
                files.Add(CreateFile(
                    (index + 1).ToString("x32"),
                    Path.Combine(root, "Background", index.ToString("D3") + ".bms"),
                    (index + 101).ToString("x64")));
            }
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new TestFileMutationService(),
                new RecordingDialogService())
            {
                BMSFiles = files,
                BmsonSongs = []
            };
            // sourceなしのfixture seedは本番保存契約を検証しないため、一つのtransactionにまとめて共通DBロックの保持時間を短縮する。
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                foreach (TestableBmsFile file in files)
                {
                    songDb.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                }
            });

            List<string> hashWork = [];
            List<string> playlistWork = [];
            List<string> installedWork = [];
            library.OwnedChartHashIndexStoreWorkObserver = hashWork.Add;
            library.PlaylistLibraryResolveIndexStoreWorkObserver = playlistWork.Add;
            library.InstalledChartLookupStoreWorkObserver = installedWork.Add;
            DuplicateMergeMaintenanceReceipt coldReceipt = library.MergeChartDirectory(source, destination, operationId: 1);
            Assert.IsFalse(coldReceipt.MergeApplied);
            Assert.AreEqual(sourceExists, Directory.Exists(source));
            Assert.IsFalse(coldReceipt.SessionReceipt.DurableCommit);
            Assert.AreEqual(0, coldReceipt.SessionReceipt.ConfirmedChangeCount);
            Assert.IsTrue(Directory.Exists(destination));
            Assert.AreEqual(files.Count, library.BMSFiles.Count);
            // ここはSELECT専用の観測なので、writer接続を保持せずread-only入口を使う。
            using (LR2SongDBExtended coldVerifyDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(files.Count, coldVerifyDb.Table<LR2SongDB.song>().Count());
            }
            Assert.AreEqual(0, hashWork.Count(operation => operation == "owned_hash_source_enumeration"));
            Assert.AreEqual(
                0,
                playlistWork.Count(operation => operation == "playlist_resolve_source_enumeration" || operation == "playlist_resolve_full_root_enumeration"));
            Assert.AreEqual(
                0,
                installedWork.Count(operation => operation == "installed_primary_hash_count_update"),
                "sourceなしmerge後にinstalled lookupを構築しました。");
            Assert.IsFalse(
                library.IsInstalledPrimaryHashLookupInitializedForDiagnostics(),
                "sourceなしmerge後にinstalled primary lookupを単独構築しました。");

            OwnedChartHashIndexVersionedSnapshot initialHash = library.GetOwnedChartHashIndexSnapshot();
            BMSLibrary.InstalledPrimaryHashWarmupResult initialPrimary = library.WarmInstalledPrimaryHashLookup("empty_source_merge_before");
            InstalledChartLookupIndexSnapshot initialInstalled = library.CreateInstalledChartLookupSnapshotForDiagnostics();
            PlaylistLibraryResolveIndexSnapshot initialPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                System.Threading.CancellationToken.None,
                out bool initialPlaylistCacheHit,
                out int initialPlaylistStaleRetries);
            Assert.IsFalse(initialPlaylistCacheHit);
            Assert.AreEqual(0, initialPlaylistStaleRetries);
            Assert.IsFalse(initialPrimary.FullDirectoryLookupInitialized);
            hashWork.Clear();
            playlistWork.Clear();
            installedWork.Clear();

            DuplicateMergeMaintenanceReceipt warmReceipt = library.MergeChartDirectory(source, destination, operationId: 2);
            Assert.IsFalse(warmReceipt.MergeApplied);
            Assert.IsFalse(warmReceipt.SessionReceipt.DurableCommit);
            Assert.AreEqual(0, warmReceipt.SessionReceipt.ConfirmedChangeCount);
            Assert.AreSame(initialHash, library.GetOwnedChartHashIndexSnapshot());
            Assert.AreSame(initialInstalled, library.CreateInstalledChartLookupSnapshotForDiagnostics());
            BMSLibrary.InstalledPrimaryHashWarmupResult updatedPrimary = library.WarmInstalledPrimaryHashLookup("empty_source_merge_after");
            PlaylistLibraryResolveIndexSnapshot updatedPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                System.Threading.CancellationToken.None,
                out bool updatedPlaylistCacheHit,
                out int updatedPlaylistStaleRetries);
            Assert.AreEqual("cached", updatedPrimary.Status);
            Assert.AreEqual(0L, updatedPrimary.BuildMs);
            Assert.IsTrue(updatedPlaylistCacheHit);
            Assert.AreEqual(0, updatedPlaylistStaleRetries);
            Assert.AreSame(initialPlaylist, updatedPlaylist);
            Assert.AreEqual(0, hashWork.Count(operation => operation == "owned_hash_source_enumeration"));
            Assert.AreEqual(
                0,
                playlistWork.Count(operation => operation == "playlist_resolve_source_enumeration" || operation == "playlist_resolve_full_root_enumeration"));
            Assert.AreEqual(
                0,
                installedWork.Count(operation => operation == "installed_primary_hash_count_update"),
                "warm sourceなしmerge後にinstalled lookupへ更新を加えました。");
        });
    }

    /// <summary>
    /// Merge の source cleanup と移動を同じ library で連続実行しても、warm な
    /// 所持 hash / installed / playlist resolve lookup を次回利用へ押し出さない。
    /// 背景16件は対象差分のprimary hash更新（最大8件）を上回り、全件列挙なしの実observerを
    /// 検出できます。背景件数による時間・仕事量比較はこの契約に含めません。
    /// </summary>
    [DataTestMethod]
    [DataRow(16)]
    public void MergeChartDirectory_TwoWarmOperationsKeepIndexesCurrentWithoutFullRebuild(int backgroundCount)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string? duplicateHash = null;
            List<TestableBmsFile> files = [];
            for (int index = 0; index < backgroundCount; index++)
            {
                files.Add(CreateFile(
                    (index + 100).ToString("x32"),
                    Path.Combine(root, "Background", index.ToString("D3") + ".bms"),
                    (index + 1000).ToString("x64")));
            }

            var operations = new List<(string Source, string Destination, TestableBmsFile Duplicate, TestableBmsFile Unique, TestableBmsFile DestinationDuplicate)>();
            for (int index = 1; index <= 2; index++)
            {
                string source = Path.Combine(root, "Source" + index);
                string destination = Path.Combine(root, "Destination" + index);
                Directory.CreateDirectory(source);
                Directory.CreateDirectory(destination);
                string duplicateSourcePath = Path.Combine(source, "duplicate.bms");
                string uniqueSourcePath = Path.Combine(source, "unique.bms");
                string destinationDuplicatePath = Path.Combine(destination, "duplicate.bms");
                File.WriteAllText(duplicateSourcePath, "#PLAYER 1\r\n#TITLE source duplicate\r\n");
                File.WriteAllText(uniqueSourcePath, "#PLAYER 1\r\n#TITLE source unique " + index + "\r\n");
                File.WriteAllText(destinationDuplicatePath, "#PLAYER 1\r\n#TITLE source duplicate\r\n");
                TestableBmsFile duplicate = CreateParsedFile(duplicateSourcePath);
                TestableBmsFile unique = CreateParsedFile(uniqueSourcePath);
                TestableBmsFile destinationDuplicate = CreateParsedFile(destinationDuplicatePath);
                duplicateHash ??= duplicate.hash;
                files.Add(duplicate);
                files.Add(unique);
                files.Add(destinationDuplicate);
                operations.Add((source, destination, duplicate, unique, destinationDuplicate));
            }

            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new TestFileMutationService(),
                new RecordingDialogService())
            {
                BMSFiles = files,
                BmsonSongs = []
            };
            // warm操作前のfixture seedは本番保存契約を検証しないため、一つのtransactionにまとめて共通DBロックの保持時間を短縮する。
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                foreach (TestableBmsFile file in files)
                {
                    songDb.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                }
            });

            BMSLibrary.InstalledPrimaryHashWarmupResult initialPrimary = library.WarmInstalledPrimaryHashLookup("two_warm_operations_before");
            Assert.IsFalse(initialPrimary.FullDirectoryLookupInitialized);
            OwnedChartHashIndexVersionedSnapshot initialHash = library.GetOwnedChartHashIndexSnapshot();
            InstalledChartLookupIndexSnapshot initialInstalled = library.CreateInstalledChartLookupSnapshotForDiagnostics();
            PlaylistLibraryResolveIndexSnapshot initialPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                System.Threading.CancellationToken.None,
                out bool initialPlaylistCacheHit,
                out int initialPlaylistStaleRetries);
            Assert.IsFalse(initialPlaylistCacheHit);
            Assert.AreEqual(0, initialPlaylistStaleRetries);
            Assert.IsTrue(initialHash.ContainsMd5(duplicateHash!));
            Assert.IsTrue(initialInstalled.ContainsPrimaryHash(duplicateHash!));
            (LibraryChartKind Kind, string Path, string Md5, string Sha256)[] initialDuplicateCandidates =
                CapturePlaylistCandidateFacts(initialPlaylist.GetMd5Candidates(duplicateHash!));
            Assert.IsTrue(initialDuplicateCandidates.Length > 0);
            Assert.IsTrue(initialDuplicateCandidates.Any(candidate =>
                string.Equals(candidate.Path, operations[0].Duplicate.path, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Md5, operations[0].Duplicate.hash, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Sha256, operations[0].Duplicate.sha256, StringComparison.OrdinalIgnoreCase)));
            LibraryChartRef initialDuplicateRepresentative =
                initialPlaylist.ResolveChartForPlaylistHash(duplicateHash!, null);
            Assert.IsNotNull(initialDuplicateRepresentative);
            string initialDuplicateRepresentativePath = initialDuplicateRepresentative!.Path;
            string initialDuplicateRepresentativeMd5 = initialDuplicateRepresentative.Md5;
            string initialDuplicateRepresentativeSha256 = initialDuplicateRepresentative.Sha256;

            List<string> hashWork = [];
            List<string> playlistWork = [];
            List<string> installedWork = [];
            library.OwnedChartHashIndexStoreWorkObserver = hashWork.Add;
            library.PlaylistLibraryResolveIndexStoreWorkObserver = playlistWork.Add;
            library.InstalledChartLookupStoreWorkObserver = installedWork.Add;
            int ownedCollectionPublicationCount = 0;
            library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(BMSLibrary.OwnedChartCollectionVersion))
                {
                    ownedCollectionPublicationCount++;
                }
            };
            for (int operationIndex = 0; operationIndex < operations.Count; operationIndex++)
            {
                (string source, string destination, TestableBmsFile duplicate, TestableBmsFile unique, TestableBmsFile destinationDuplicate) = operations[operationIndex];
                string sourceDuplicatePath = Path.Combine(source, "duplicate.bms");
                string sourceUniquePath = Path.Combine(source, "unique.bms");
                string destinationDuplicatePath = Path.Combine(destination, "duplicate.bms");
                string destinationUniquePath = Path.Combine(destination, "unique.bms");
                DuplicateMergeMaintenanceReceipt receipt = library.MergeChartDirectory(
                    source,
                    destination,
                    operationId: operationIndex + 1);

                Assert.IsTrue(receipt.MergeApplied, receipt.SessionReceipt.PrimaryFailure?.ToString());
                Assert.AreEqual(1, receipt.SessionReceipt.ConfirmedChangeCount);
                Assert.AreEqual(1, receipt.SessionReceipt.CatalogChartPathChangeCount);
                Assert.AreEqual(1, receipt.SessionReceipt.CatalogChartRemovalCount);
                Assert.AreEqual(operationIndex + 1, ownedCollectionPublicationCount);
                Assert.IsFalse(Directory.Exists(source));
                Assert.IsTrue(File.Exists(destinationDuplicate.path));
                Assert.IsTrue(File.Exists(destinationUniquePath));

                // ここはSELECT専用の観測なので、writer接続を保持せずread-only入口を使う。
                using (LR2SongDBExtended verifyDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
                {
                    string[] dbPaths = verifyDb.Table<LR2SongDB.song>().Select(row => row.path).ToArray();
                    Assert.IsFalse(dbPaths.Contains(sourceDuplicatePath, StringComparer.OrdinalIgnoreCase));
                    Assert.IsFalse(dbPaths.Contains(sourceUniquePath, StringComparer.OrdinalIgnoreCase));
                    Assert.IsTrue(dbPaths.Contains(destinationDuplicatePath, StringComparer.OrdinalIgnoreCase));
                    Assert.IsTrue(dbPaths.Contains(destinationUniquePath, StringComparer.OrdinalIgnoreCase));
                    foreach (TestableBmsFile backgroundFile in files.Take(backgroundCount))
                    {
                        Assert.IsTrue(
                            dbPaths.Contains(backgroundFile.path, StringComparer.OrdinalIgnoreCase),
                            "merge後も未対象のbackground rowを保持します。");
                    }
                }

                OwnedChartHashIndexVersionedSnapshot updatedHash = library.GetOwnedChartHashIndexSnapshot();
                OwnedChartHashIndexVersionedSnapshot cachedHash = library.GetOwnedChartHashIndexSnapshot();
                InstalledChartLookupIndexSnapshot updatedInstalled = library.CreateInstalledChartLookupSnapshotForDiagnostics();
                InstalledChartLookupIndexSnapshot cachedInstalled = library.CreateInstalledChartLookupSnapshotForDiagnostics();
                BMSLibrary.InstalledPrimaryHashWarmupResult updatedPrimary = library.WarmInstalledPrimaryHashLookup("two_warm_operations_after");
                PlaylistLibraryResolveIndexSnapshot updatedPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                    System.Threading.CancellationToken.None,
                    out bool updatedPlaylistCacheHit,
                    out int updatedPlaylistStaleRetries);
                PlaylistLibraryResolveIndexSnapshot cachedPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                    System.Threading.CancellationToken.None,
                    out bool cachedPlaylistCacheHit,
                    out int cachedPlaylistStaleRetries);

                Assert.AreSame(updatedHash, cachedHash);
                Assert.IsTrue(updatedHash.ContainsMd5(duplicateHash!));
                Assert.IsTrue(updatedHash.ContainsMd5(unique.hash));
                Assert.IsTrue(updatedInstalled.ContainsPrimaryHash(unique.hash));
                Assert.IsFalse(updatedInstalled
                    .GetDistinctDirectoriesByPrimaryHash(unique.hash)
                    .Contains(source, StringComparer.OrdinalIgnoreCase));
                Assert.IsTrue(updatedInstalled
                    .GetDistinctDirectoriesByPrimaryHash(unique.hash)
                    .Contains(destination, StringComparer.OrdinalIgnoreCase));
                Assert.AreSame(updatedInstalled, cachedInstalled);
                Assert.IsTrue(updatedPrimary.FullDirectoryLookupInitialized);
                Assert.AreEqual("cached", updatedPrimary.Status);
                Assert.AreEqual(0L, updatedPrimary.BuildMs);
                Assert.IsTrue(updatedPlaylistCacheHit);
                Assert.AreEqual(0, updatedPlaylistStaleRetries);
                Assert.IsTrue(cachedPlaylistCacheHit);
                Assert.AreEqual(0, cachedPlaylistStaleRetries);
                Assert.AreSame(updatedPlaylist, cachedPlaylist);
                Assert.IsFalse(updatedPlaylist.ContainsCandidate(LibraryChartKind.Bms, sourceDuplicatePath));
                Assert.IsTrue(updatedPlaylist.ContainsCandidate(
                    LibraryChartKind.Bms,
                    Path.Combine(destination, "duplicate.bms")));
                Assert.IsFalse(updatedPlaylist.ContainsCandidate(LibraryChartKind.Bms, sourceUniquePath));
                Assert.IsTrue(updatedPlaylist.ContainsCandidate(
                    LibraryChartKind.Bms,
                    Path.Combine(destination, "unique.bms")));
                Assert.IsTrue(CapturePlaylistCandidateFacts(updatedPlaylist.GetMd5Candidates(unique.hash))
                    .Any(candidate =>
                        candidate.Kind == LibraryChartKind.Bms
                        && string.Equals(candidate.Path, destinationUniquePath, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(candidate.Md5, unique.hash, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(candidate.Sha256, unique.sha256, StringComparison.OrdinalIgnoreCase)));
                Assert.IsNotNull(initialPlaylist.ResolveChartForPlaylistHash(duplicateHash, null));
                Assert.AreEqual(
                    initialDuplicateRepresentativePath,
                    initialPlaylist.ResolveChartForPlaylistHash(duplicateHash!, null)!.Path);
                Assert.AreEqual(
                    initialDuplicateRepresentativeMd5,
                    initialPlaylist.ResolveChartForPlaylistHash(duplicateHash!, null)!.Md5);
                Assert.AreEqual(
                    initialDuplicateRepresentativeSha256,
                    initialPlaylist.ResolveChartForPlaylistHash(duplicateHash!, null)!.Sha256);
                CollectionAssert.AreEqual(
                    initialDuplicateCandidates,
                    CapturePlaylistCandidateFacts(initialPlaylist.GetMd5Candidates(duplicateHash!)));
                Assert.IsTrue(initialInstalled.GetDistinctDirectoriesByPrimaryHash(duplicateHash!).Count > 0);
            }

            Assert.AreEqual(
                0,
                hashWork.Count(operation => operation == "owned_hash_source_enumeration"),
                "warm merge後のowned hash getterがsource全体を再列挙しました。");
            Assert.AreEqual(
                0,
                playlistWork.Count(operation => operation == "playlist_resolve_source_enumeration" || operation == "playlist_resolve_full_root_enumeration"),
                "warm merge後のplaylist resolve getterがsource全体を再列挙しました。");
            Assert.IsTrue(
                installedWork.Count(operation => operation == "installed_primary_hash_count_update") <= 8,
                "warm merge後にinstalled lookupを全件再構築しました。");
            Assert.IsTrue(
                installedWork.Count(operation => operation == "installed_primary_hash_count_update") > 0,
                "実mergeのinstalled lookup差分更新を観測できませんでした。");
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

    private static TestableBmsFile CreateParsedFile(string path)
    {
        var parsed = BMSFile.CreateBMSFileFromFile(path);
        return CreateFile(parsed.hash, path, parsed.sha256);
    }

    private static (LibraryChartKind Kind, string Path, string Md5, string Sha256)[] CapturePlaylistCandidateFacts(
        IEnumerable<LibraryChartRef> candidates)
    {
        return [.. (candidates ?? [])
            .Where(candidate => candidate != null)
            .Select(candidate => (candidate.Kind, candidate.Path, candidate.Md5, candidate.Sha256))];
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
            // fixture schemaは初期化処理の検証対象ではないため、一つのtransactionにまとめて共通DBロックの保持時間を短縮する。
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
            });
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
        public Action? OnShow { get; set; }

        public bool ThrowOnShow { get; set; }

        public int ShowCount { get; private set; }

        public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            ShowCount++;
            OnShow?.Invoke();
            if (ThrowOnShow)
            {
                throw new InvalidOperationException("The test dialog effect failed.");
            }
            return defaultResult == MessageBoxResult.None ? MessageBoxResult.OK : defaultResult;
        }
    }

    private sealed class TestFileMutationService : IFileMutationService
    {
        public int FailNextMutationCount { get; set; }

        public Action<string>? AfterDeleteDirectory { get; set; }

        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null!)
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            ThrowForConfiguredMutation();
            string? destinationDirectory = Path.GetDirectoryName(destinationPath);
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
            ThrowForConfiguredMutation();
            string? destinationParent = Path.GetDirectoryName(destinationPath);
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
            ThrowForConfiguredMutation();
            string? destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }
            File.Copy(sourcePath, destinationPath, overwrite);
        }

        public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            ThrowForConfiguredMutation();
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
                AfterDeleteDirectory?.Invoke(directoryPath);
            }
        }

        public void DeleteDirectoryShell(string directoryPath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            DeleteDirectoryDirect(directoryPath, recursive: true, options);
        }

        public void SetTimestamps(string path, bool isDirectory, System.DateTime? creationTime, System.DateTime? lastWriteTime, FileMutationOptions options = null!)
        {
        }

        private void ThrowForConfiguredMutation()
        {
            if (FailNextMutationCount <= 0)
            {
                return;
            }

            FailNextMutationCount--;
            throw new IOException("The test file mutation failed.");
        }
    }
}
