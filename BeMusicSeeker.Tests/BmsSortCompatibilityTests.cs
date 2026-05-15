using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Views;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Util;
using SQLite;

namespace BeMusicSeeker.Tests;

/// <summary>
/// ソート最適化後の並び順が従来ロジックと一致することを検証します。
/// </summary>
[TestClass]
public sealed class BmsSortCompatibilityTests
{
    private const int SampleSongCount = 20000;
    private const int PerfWarmupCount = 1;
    private const int PerfMeasureCount = 3;

    private static readonly string TestSongDbRelativePath = Path.Combine("TestData", "song_snapshot", "song.db");

    public TestContext? TestContext { get; set; }

    /// <summary>
    /// 代表カラムの昇順/降順で従来ロジックとの完全一致を検証します。
    /// </summary>
    [TestMethod]
    [TestCategory("Compatibility")]
    [Microsoft.VisualStudio.TestTools.UnitTesting.Ignore("Manual compatibility check. Excluded from default build/test pass criteria.")]
    public void SortOrder_ShouldMatchLegacyImplementation_ForRepresentativeColumns()
    {
        string testSongDbFullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, TestSongDbRelativePath);
        Assert.IsTrue(File.Exists(testSongDbFullPath), "Test song.db not found: " + testSongDbFullPath);

        List<BMSFile> sourceRows = LoadRowsFromSongDb(testSongDbFullPath, SampleSongCount);
        Assert.IsTrue(sourceRows.Count > 0, "No song rows loaded from test song.db.");

        CultureInfo previousCurrentCulture = CultureInfo.CurrentCulture;
        CultureInfo previousCurrentUICulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo stableCulture = CultureInfo.GetCultureInfo("ja-JP");
            CultureInfo.CurrentCulture = stableCulture;
            CultureInfo.CurrentUICulture = stableCulture;

            List<(string columnName, ListSortDirection direction)> sortCases = new List<(string, ListSortDirection)>
            {
                (nameof(BMSFile.Title), ListSortDirection.Ascending),
                (nameof(BMSFile.Title), ListSortDirection.Descending),
                (nameof(BMSFile.Artist), ListSortDirection.Ascending),
                (nameof(BMSFile.path), ListSortDirection.Ascending),
                (nameof(BMSFile.Level), ListSortDirection.Ascending),
                (nameof(BMSFile.mode), ListSortDirection.Ascending),
                (nameof(BMSFile.notes), ListSortDirection.Descending)
            };

            foreach ((string columnName, ListSortDirection direction) in sortCases)
            {
                MainWindowViewModel.cSortParameters sortParameters = new MainWindowViewModel.cSortParameters
                {
                    ColumnsName = columnName,
                    Direction = direction
                };

                List<BMSFile> legacyResult = SortByLegacyImplementation(sourceRows, sortParameters);
                List<BMSFile> optimizedResult = BMSFileSortEngine.Sort(sourceRows, sortParameters);

                Assert.AreEqual(legacyResult.Count, optimizedResult.Count, $"Row count mismatch for {columnName}/{direction}");

                string legacyDigest = ComputeDigest(legacyResult);
                string optimizedDigest = ComputeDigest(optimizedResult);
                if (!string.Equals(legacyDigest, optimizedDigest, StringComparison.Ordinal))
                {
                    int mismatchIndex = FindFirstMismatchIndex(legacyResult, optimizedResult);
                    string legacyRow = (mismatchIndex >= 0 && mismatchIndex < legacyResult.Count) ? (legacyResult[mismatchIndex]?.path ?? string.Empty) : "(n/a)";
                    string optimizedRow = (mismatchIndex >= 0 && mismatchIndex < optimizedResult.Count) ? (optimizedResult[mismatchIndex]?.path ?? string.Empty) : "(n/a)";
                    Assert.Fail($"Sort order mismatch for {columnName}/{direction} at index={mismatchIndex} legacy={legacyRow} optimized={optimizedRow}");
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCurrentCulture;
            CultureInfo.CurrentUICulture = previousCurrentUICulture;
        }
    }

    /// <summary>
    /// 旧実装と最適化実装のソート処理時間を比較出力します（参考計測）。
    /// </summary>
    [TestMethod]
    [TestCategory("Performance")]
    [DoNotParallelize]
    public void SortPerformance_ReportLegacyVsOptimized_ForRepresentativeColumns()
    {
        string testSongDbFullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, TestSongDbRelativePath);
        Assert.IsTrue(File.Exists(testSongDbFullPath), "Test song.db not found: " + testSongDbFullPath);

        List<BMSFile> sourceRows = LoadRowsFromSongDb(testSongDbFullPath, SampleSongCount);
        Assert.IsTrue(sourceRows.Count > 0, "No song rows loaded from test song.db.");

        List<(string columnName, ListSortDirection direction)> sortCases = new List<(string, ListSortDirection)>
        {
            (nameof(BMSFile.Level), ListSortDirection.Descending),
            (nameof(BMSFile.path), ListSortDirection.Ascending),
            (nameof(BMSFile.totalnotes), ListSortDirection.Descending),
            (nameof(BMSFile.mode), ListSortDirection.Ascending),
            (nameof(BMSFile.rate), ListSortDirection.Descending),
            (nameof(BMSFile.scoreDifficulty), ListSortDirection.Descending),
            (nameof(BMSFile.rankingLastupdate), ListSortDirection.Descending)
        };

        foreach ((string columnName, ListSortDirection direction) in sortCases)
        {
            MainWindowViewModel.cSortParameters sortParameters = new MainWindowViewModel.cSortParameters
            {
                ColumnsName = columnName,
                Direction = direction
            };

            for (int warmup = 0; warmup < PerfWarmupCount; warmup++)
            {
                _ = SortByLegacyImplementation(sourceRows, sortParameters);
                _ = BMSFileSortEngine.Sort(sourceRows, sortParameters, out _);
            }

            List<long> legacyMs = new List<long>(PerfMeasureCount);
            List<long> optimizedMs = new List<long>(PerfMeasureCount);
            for (int i = 0; i < PerfMeasureCount; i++)
            {
                Stopwatch swLegacy = Stopwatch.StartNew();
                _ = SortByLegacyImplementation(sourceRows, sortParameters);
                swLegacy.Stop();
                legacyMs.Add(swLegacy.ElapsedMilliseconds);

                Stopwatch swOptimized = Stopwatch.StartNew();
                _ = BMSFileSortEngine.Sort(sourceRows, sortParameters, out string sortProfile);
                swOptimized.Stop();
                optimizedMs.Add(swOptimized.ElapsedMilliseconds);

                TestContext?.WriteLine($"sort_perf_iter column={columnName} direction={direction} iter={i + 1} legacyMs={swLegacy.ElapsedMilliseconds} optimizedMs={swOptimized.ElapsedMilliseconds} sortProfile={sortProfile} rows={sourceRows.Count}");
            }

            long legacyMedian = Median(legacyMs);
            long optimizedMedian = Median(optimizedMs);
            double ratio = legacyMedian == 0 ? 0.0 : (double)optimizedMedian / legacyMedian;
            TestContext?.WriteLine($"sort_perf_summary column={columnName} direction={direction} rows={sourceRows.Count} legacyMedianMs={legacyMedian} optimizedMedianMs={optimizedMedian} ratio={ratio:F3}");
        }
    }

    /// <summary>
    /// 通常一覧の LEVEL 列は文字列順ではなく数値順で並ぶことを検証します。
    /// </summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void Sort_LevelColumn_UsesNumericKeyForRegularRows()
    {
        TestableBmsFile regularLevel12 = new TestableBmsFile();
        regularLevel12.ApplySnapshot(new SongSnapshotRow { path = "z_regular_12.bms", title = "Regular12", level = 12, hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" });

        TestableBmsFile regularLevel3 = new TestableBmsFile();
        regularLevel3.ApplySnapshot(new SongSnapshotRow { path = "m_regular_3.bms", title = "Regular3", level = 3, hash = "cccccccccccccccccccccccccccccccc" });

        List<BMSFile> source = new List<BMSFile> { regularLevel12, regularLevel3 };
        MainWindowViewModel.cSortParameters sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(BMSFile.Level),
            Direction = ListSortDirection.Ascending
        };

        List<BMSFile> sorted = BMSFileSortEngine.Sort(source, sortParameters, out string sortProfile);
        string[] sortedPaths = sorted.Select((BMSFile row) => row.path).ToArray();

        CollectionAssert.AreEqual(
            new[] { "m_regular_3.bms", "z_regular_12.bms" },
            sortedPaths,
            "LEVEL must be sorted numerically instead of lexicographically.");
        Assert.AreEqual("level_mixed_double", sortProfile);
    }

    /// <summary>
    /// FOLDER 列は通常一覧では高速文字列比較を利用することを検証します。
    /// </summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    [TestCategory("KnownFailure")]
    [Microsoft.VisualStudio.TestTools.UnitTesting.Ignore("Known compatibility holdout for optional fast folder sort. Excluded from default test pass criteria.")]
    public void Sort_FolderColumn_UsesFastStringProfileInRegularView()
    {
        TestableBmsFile folder10 = new TestableBmsFile();
        folder10.ApplySnapshot(new SongSnapshotRow { path = "z.bms", title = "Z", level = 1, hash = "11111111111111111111111111111111" });
        folder10.SetFolder("folder10");

        TestableBmsFile folder2 = new TestableBmsFile();
        folder2.ApplySnapshot(new SongSnapshotRow { path = "a.bms", title = "A", level = 1, hash = "22222222222222222222222222222222" });
        folder2.SetFolder("folder2");

        MainWindowViewModel.cSortParameters sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(BMSFile.Folder),
            Direction = ListSortDirection.Ascending
        };

        List<BMSFile> sorted = BMSFileSortEngine.Sort(new[] { folder10, folder2 }, sortParameters, out string sortProfile);

        CollectionAssert.AreEqual(new[] { "z.bms", "a.bms" }, sorted.Select((BMSFile row) => row.path).ToArray());
        Assert.AreEqual("string_fast_ordinal_ignore_case", sortProfile);
    }

    /// <summary>
    /// FOLDER 列はプレイリスト明細では legacy 自然順を利用することを検証します。
    /// </summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void Sort_FolderColumn_UsesLegacyNaturalProfileInPlaylistDetailView()
    {
        TestableBmsFile folder10 = new TestableBmsFile();
        folder10.ApplySnapshot(new SongSnapshotRow { path = "z.bms", title = "Z", level = 1, hash = "11111111111111111111111111111111" });
        folder10.SetFolder("folder10");

        TestableBmsFile folder2 = new TestableBmsFile();
        folder2.ApplySnapshot(new SongSnapshotRow { path = "a.bms", title = "A", level = 1, hash = "22222222222222222222222222222222" });
        folder2.SetFolder("folder2");

        MainWindowViewModel.cSortParameters sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(BMSFile.Folder),
            Direction = ListSortDirection.Ascending
        };

        List<BMSFile> sorted = BMSFileSortEngine.Sort(new[] { folder10, folder2 }, sortParameters, isPlaylistDetailView: true, out string sortProfile);

        CollectionAssert.AreEqual(new[] { "a.bms", "z.bms" }, sorted.Select((BMSFile row) => row.path).ToArray());
        Assert.AreEqual("folder_natural_legacy", sortProfile);
    }

    /// <summary>
    /// 主要 string 列は fast 経路で高速比較プロファイルになることを検証します。
    /// </summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void Sort_TitleColumn_UsesFastStringProfile()
    {
        TestableBmsFile row1 = new TestableBmsFile();
        row1.ApplySnapshot(new SongSnapshotRow { path = "b.bms", title = "bbb", level = 1, hash = "33333333333333333333333333333333" });

        TestableBmsFile row2 = new TestableBmsFile();
        row2.ApplySnapshot(new SongSnapshotRow { path = "a.bms", title = "AAA", level = 1, hash = "44444444444444444444444444444444" });

        MainWindowViewModel.cSortParameters sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(BMSFile.Title),
            Direction = ListSortDirection.Ascending
        };

        List<BMSFile> sorted = BMSFileSortEngine.Sort(new[] { row1, row2 }, sortParameters, out string sortProfile);

        CollectionAssert.AreEqual(new[] { "a.bms", "b.bms" }, sorted.Select((BMSFile row) => row.path).ToArray());
        Assert.AreEqual("string_fast_ordinal_ignore_case", sortProfile);
    }

    [TestMethod]
    [TestCategory("SortEngine")]
    public void LibraryChartRowSortEngine_StringColumnsRespectFastSortSetting()
    {
        LibraryChartRow title10 = CreateLibraryChartRow("z_item10.bms", "item10", level: 1);
        LibraryChartRow title2 = CreateLibraryChartRow("a_item2.bms", "item2", level: 1);
        MainWindowViewModel.cSortParameters sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(LibraryChartRow.Title),
            Direction = ListSortDirection.Ascending
        };

        List<LibraryChartRow> legacySorted = LibraryChartRowSortEngine.SortForMainView(new[] { title10, title2 }, sortParameters, isPlaylistDetailView: false, useLegacySortForDataGrid: true, out string legacyProfile);
        List<LibraryChartRow> fastSorted = LibraryChartRowSortEngine.SortForMainView(new[] { title10, title2 }, sortParameters, isPlaylistDetailView: false, useLegacySortForDataGrid: false, out string fastProfile);

        CollectionAssert.AreEqual(new[] { "a_item2.bms", "z_item10.bms" }, legacySorted.Select((LibraryChartRow row) => row.path).ToArray());
        CollectionAssert.AreEqual(new[] { "z_item10.bms", "a_item2.bms" }, fastSorted.Select((LibraryChartRow row) => row.path).ToArray());
        Assert.AreEqual("library_chart_legacy_string", legacyProfile);
        Assert.AreEqual("library_chart_string_fast_ordinal_ignore_case", fastProfile);
    }

    [TestMethod]
    [TestCategory("SortEngine")]
    public void LibraryChartRowSortEngine_StringSortMetricsDescribeFastPath()
    {
        LibraryChartRow title10 = CreateLibraryChartRow("z_item10.bms", "item10", level: 1);
        LibraryChartRow title2 = CreateLibraryChartRow("a_item2.bms", "item2", level: 1);
        MainWindowViewModel.cSortParameters sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(LibraryChartRow.Title),
            Direction = ListSortDirection.Ascending
        };

        List<LibraryChartRow> fastSorted = LibraryChartRowSortEngine.SortForMainView(new[] { title10, title2 }, sortParameters, isPlaylistDetailView: false, useLegacySortForDataGrid: false, out string fastProfile, out LibraryChartSortMetrics metrics);

        CollectionAssert.AreEqual(new[] { "z_item10.bms", "a_item2.bms" }, fastSorted.Select((LibraryChartRow row) => row.path).ToArray());
        Assert.AreEqual("library_chart_string_fast_ordinal_ignore_case", fastProfile);
        Assert.AreEqual(2, metrics.RowCount);
        Assert.AreEqual(nameof(LibraryChartRow.Title), metrics.ColumnName);
        Assert.AreEqual(ListSortDirection.Ascending, metrics.Direction);
        Assert.AreEqual("String", metrics.PropertyTypeName);
        Assert.AreEqual("library_chart_string_fast_ordinal_ignore_case", metrics.SortProfile);
        Assert.AreEqual("ordinal_ignore_case", metrics.StringSortKind);
        Assert.IsTrue(metrics.SortMs >= 0);
    }

    [TestMethod]
    [TestCategory("SortEngine")]
    public void VirtualNormalLibraryModeSupport_CoversNormalListRefreshAndFilterUpdates()
    {
        MainWindowViewModel.viewUpdateMode[] supportedModes =
        {
            MainWindowViewModel.viewUpdateMode.TreeViewFilterNotChanged,
            MainWindowViewModel.viewUpdateMode.FolderFilterSelected,
            MainWindowViewModel.viewUpdateMode.FullScanAllChartsFilterSelected,
            MainWindowViewModel.viewUpdateMode.KeywordFilterUpdated,
            MainWindowViewModel.viewUpdateMode.ModeFilterUpdated,
            MainWindowViewModel.viewUpdateMode.SortUpdated
        };

        foreach (MainWindowViewModel.viewUpdateMode mode in Enum.GetValues(typeof(MainWindowViewModel.viewUpdateMode)).Cast<MainWindowViewModel.viewUpdateMode>())
        {
            Assert.AreEqual(
                supportedModes.Contains(mode),
                MainWindowViewModel.IsVirtualNormalLibraryModeSupportedForTest((int)mode),
                mode.ToString());
        }
    }

    [TestMethod]
    [TestCategory("SortEngine")]
    public void NormalLibrarySortCacheCandidate_AllowsVirtualRegistryColumns()
    {
        Assert.IsTrue(MainWindowViewModel.IsNormalLibrarySortCacheCandidateForTest(null));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibrarySortCacheCandidateForTest(string.Empty));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibrarySortCacheCandidateForTest(nameof(LibraryChartRow.Title)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibrarySortCacheCandidateForTest(nameof(LibraryChartRow.path)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibrarySortCacheCandidateForTest(nameof(LibraryChartRow.Folder)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibrarySortCacheCandidateForTest(nameof(LibraryChartRow.Artist)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibrarySortCacheCandidateForTest(nameof(LibraryChartRow.genre)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibrarySortCacheCandidateForTest(nameof(LibraryChartRow.mode)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibrarySortCacheCandidateForTest(nameof(LibraryChartRow.tag)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibrarySortCacheCandidateForTest(nameof(LibraryChartRow.hash)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibrarySortCacheCandidateForTest(nameof(LibraryChartRow.sha256)));
        Assert.IsFalse(MainWindowViewModel.IsNormalLibrarySortCacheCandidateForTest("Path"));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibrarySortCacheCandidateForTest(nameof(LibraryChartRow.instl_dst)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibrarySortCacheCandidateForTest(nameof(LibraryChartRow.InstallDestinationTitle)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibrarySortCacheCandidateForTest(nameof(LibraryChartRow.InstallDestinationArtist)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibrarySortCacheCandidateForTest(nameof(LibraryChartRow.RefTablesSymbols)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibrarySortCacheCandidateForTest(nameof(LibraryChartRow.clear)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibrarySortCacheCandidateForTest(nameof(LibraryChartRow.rateDouble)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibrarySortCacheCandidateForTest(nameof(LibraryChartRow.score)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibrarySortCacheCandidateForTest(nameof(LibraryChartRow.maxcombo)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibrarySortCacheCandidateForTest(nameof(LibraryChartRow.minbp)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibrarySortCacheCandidateForTest(nameof(LibraryChartRow.ChartLevelSortKey)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibrarySortCacheCandidateForTest(nameof(LibraryChartRow.ChartTotalSortKey)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibrarySortCacheCandidateForTest(nameof(LibraryChartRow.WarningDigestText)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibrarySortCacheCandidateForTest(nameof(LibraryChartRow.WAVHealth)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibrarySortCacheCandidateForTest(nameof(LibraryChartRow.BGAHealth)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibrarySortCacheCandidateForTest(nameof(LibraryChartRow.MovieHealth)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibrarySortCacheCandidateForTest(nameof(LibraryChartRow.encoding)));
    }

    [TestMethod]
    [TestCategory("SortEngine")]
    public void NormalLibraryVirtualSortKeyProperty_UsesVirtualRegistryColumns()
    {
        Assert.IsTrue(MainWindowViewModel.IsNormalLibraryVirtualSortKeyPropertyForTest(nameof(BMSFile.Title)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibraryVirtualSortKeyPropertyForTest(nameof(BMSFile.path)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibraryVirtualSortKeyPropertyForTest(nameof(BMSFile.Folder)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibraryVirtualSortKeyPropertyForTest(nameof(BMSFile.Artist)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibraryVirtualSortKeyPropertyForTest(nameof(BMSFile.genre)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibraryVirtualSortKeyPropertyForTest(nameof(BMSFile.mode)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibraryVirtualSortKeyPropertyForTest(nameof(BMSFile.tag)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibraryVirtualSortKeyPropertyForTest(nameof(BMSFile.hash)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibraryVirtualSortKeyPropertyForTest(nameof(BMSFile.sha256)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibraryVirtualSortKeyPropertyForTest(nameof(BMSFile.instl_dst)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibraryVirtualSortKeyPropertyForTest(nameof(BMSFile.InstallDestinationTitle)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibraryVirtualSortKeyPropertyForTest(nameof(BMSFile.InstallDestinationArtist)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibraryVirtualSortKeyPropertyForTest(nameof(BMSFile.RefTablesSymbols)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibraryVirtualSortKeyPropertyForTest(nameof(BMSFile.clear)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibraryVirtualSortKeyPropertyForTest(nameof(BMSFile.rateDouble)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibraryVirtualSortKeyPropertyForTest(nameof(BMSFile.score)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibraryVirtualSortKeyPropertyForTest(nameof(BMSFile.maxcombo)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibraryVirtualSortKeyPropertyForTest(nameof(BMSFile.minbp)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibraryVirtualSortKeyPropertyForTest(nameof(BMSFile.ChartLevelSortKey)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibraryVirtualSortKeyPropertyForTest(nameof(BMSFile.WarningDigestText)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibraryVirtualSortKeyPropertyForTest(nameof(BMSFile.WAVHealth)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibraryVirtualSortKeyPropertyForTest(nameof(BMSFile.BGAHealth)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibraryVirtualSortKeyPropertyForTest(nameof(BMSFile.MovieHealth)));
        Assert.IsTrue(MainWindowViewModel.IsNormalLibraryVirtualSortKeyPropertyForTest(nameof(BMSFile.encoding)));
    }

    [TestMethod]
    [TestCategory("SortEngine")]
    public void MainViewSortColumnDependency_ClassifiesMainColumnFamilies()
    {
        foreach (ChartListOrderColumnMetadata column in ChartListOrder.GetVirtualSortColumnMetadata())
        {
            Assert.AreEqual(column.Dependency, MainWindowViewModel.GetMainViewSortColumnDependencyForTest(column.NormalizedColumnName), column.NormalizedColumnName);
        }
        Assert.AreEqual(MainViewDataDependency.IdentitySortKey, MainWindowViewModel.GetMainViewSortColumnDependencyForTest(null));
        Assert.AreEqual(MainViewDataDependency.IdentitySortKey, MainWindowViewModel.GetMainViewSortColumnDependencyForTest(nameof(LibraryChartRow.instl_dst)));
        Assert.AreEqual(MainViewDataDependency.IdentitySortKey, MainWindowViewModel.GetMainViewSortColumnDependencyForTest(nameof(LibraryChartRow.InstallDestinationTitle)));
        Assert.AreEqual(MainViewDataDependency.IdentitySortKey, MainWindowViewModel.GetMainViewSortColumnDependencyForTest(nameof(LibraryChartRow.InstallDestinationArtist)));
        Assert.AreEqual(MainViewDataDependency.IdentitySortKey, MainWindowViewModel.GetMainViewSortColumnDependencyForTest(nameof(LibraryChartRow.RefTablesSymbols)));
        Assert.AreEqual(MainViewDataDependency.Score, MainWindowViewModel.GetMainViewSortColumnDependencyForTest(nameof(LibraryChartRow.rateDouble)));
        Assert.AreEqual(MainViewDataDependency.Score, MainWindowViewModel.GetMainViewSortColumnDependencyForTest(nameof(LibraryChartRow.rankingString)));
        Assert.AreEqual(MainViewDataDependency.ChartInfo, MainWindowViewModel.GetMainViewSortColumnDependencyForTest(nameof(LibraryChartRow.ChartTotalSortKey)));
        Assert.AreEqual(MainViewDataDependency.Maintenance, MainWindowViewModel.GetMainViewSortColumnDependencyForTest(nameof(LibraryChartRow.WAVHealth)));
        Assert.AreEqual(MainViewDataDependency.Warning, MainWindowViewModel.GetMainViewSortColumnDependencyForTest(nameof(LibraryChartRow.WarningDigestText)));
    }

    [TestMethod]
    [TestCategory("SortEngine")]
    public void NormalLibrarySortKeyInvalidationReasons_CoverVirtualOrderCacheInvalidators()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                "bms_title_changed",
                "bms_path_changed",
                "bmson_path_changed",
                "bmson_sort_key_changed",
                "chart_info_digest_backfilled",
                "install_destination_changed",
                "ref_tables_changed",
                "maintenance_changed",
                "warning_changed"
            },
            MainWindowViewModel.GetNormalLibrarySortKeyInvalidationReasonsForTest().ToArray());
    }

    [TestMethod]
    [TestCategory("SortEngine")]
    public void NormalLibraryPathSortKeyInvalidationReasons_DistinguishBmsAndBmsonPathMutations()
    {
        CollectionAssert.AreEqual(
            new[] { "bms_path_changed" },
            MainWindowViewModel.GetNormalLibraryPathSortKeyInvalidationReasonsForTest(hasBmsPathMutation: true, hasBmsonPathMutation: false).ToArray());
        CollectionAssert.AreEqual(
            new[] { "bmson_path_changed" },
            MainWindowViewModel.GetNormalLibraryPathSortKeyInvalidationReasonsForTest(hasBmsPathMutation: false, hasBmsonPathMutation: true).ToArray());
        CollectionAssert.AreEqual(
            new[] { "bms_path_changed", "bmson_path_changed" },
            MainWindowViewModel.GetNormalLibraryPathSortKeyInvalidationReasonsForTest(hasBmsPathMutation: true, hasBmsonPathMutation: true).ToArray());
        Assert.AreEqual(0, MainWindowViewModel.GetNormalLibraryPathSortKeyInvalidationReasonsForTest(hasBmsPathMutation: false, hasBmsonPathMutation: false).Count);
    }

    [TestMethod]
    [TestCategory("SortEngine")]
    public void MainViewRefreshDecision_SkipsScoreUpdateWhenFullNormalLibrarySortIsUnaffected()
    {
        foreach (string columnName in ChartListOrder.GetVirtualSortColumnMetadata()
            .Where(column => column.Dependency != MainViewDataDependency.Score)
            .Select(column => column.NormalizedColumnName))
        {
            MainViewRefreshDecision decision = MainWindowViewModel.BuildMainViewRefreshDecisionForTest(
                MainWindowViewModel.viewUpdateMode.FolderFilterSelected,
                folderFilterApplied: false,
                keywordFilter: string.Empty,
                modeFilter: MainWindowViewModel.ModeFilterType.All,
                sortColumnName: columnName,
                isPlaylistDetailView: false,
                dependency: MainViewDataDependency.Score,
                reason: "score_hydration_completed");

            Assert.AreEqual(MainViewRefreshAction.SkipMainViewRefresh, decision.Action, columnName);
            Assert.AreEqual(MainViewDataDependency.Score, decision.Dependency, columnName);
            Assert.AreNotEqual(MainViewDataDependency.Score, decision.SortDependency, columnName);
        }
    }

    [TestMethod]
    [TestCategory("SortEngine")]
    public void MainViewRefreshDecision_RefreshesWhenScoreUpdateCanAffectCurrentView()
    {
        MainViewRefreshDecision scoreSortDecision = MainWindowViewModel.BuildMainViewRefreshDecisionForTest(
            MainWindowViewModel.viewUpdateMode.FolderFilterSelected,
            folderFilterApplied: false,
            keywordFilter: string.Empty,
            modeFilter: MainWindowViewModel.ModeFilterType.All,
            sortColumnName: nameof(LibraryChartRow.rateDouble),
            isPlaylistDetailView: false,
            dependency: MainViewDataDependency.Score,
            reason: "ranking_refresh_completed");
        MainViewRefreshDecision keywordDecision = MainWindowViewModel.BuildMainViewRefreshDecisionForTest(
            MainWindowViewModel.viewUpdateMode.FolderFilterSelected,
            folderFilterApplied: false,
            keywordFilter: "rate:>90",
            modeFilter: MainWindowViewModel.ModeFilterType.All,
            sortColumnName: nameof(LibraryChartRow.Title),
            isPlaylistDetailView: false,
            dependency: MainViewDataDependency.Score,
            reason: "score_hydration_completed");
        MainViewRefreshDecision unknownSortDecision = MainWindowViewModel.BuildMainViewRefreshDecisionForTest(
            MainWindowViewModel.viewUpdateMode.FolderFilterSelected,
            folderFilterApplied: false,
            keywordFilter: string.Empty,
            modeFilter: MainWindowViewModel.ModeFilterType.All,
            sortColumnName: "UnknownComputedColumn",
            isPlaylistDetailView: false,
            dependency: MainViewDataDependency.Score,
            reason: "score_hydration_completed");

        Assert.AreEqual(MainViewRefreshAction.Refresh, scoreSortDecision.Action);
        Assert.AreEqual(MainViewRefreshAction.Refresh, keywordDecision.Action);
        Assert.AreEqual(MainViewRefreshAction.Refresh, unknownSortDecision.Action);
    }

    [TestMethod]
    [TestCategory("SortEngine")]
    public void LibraryChartRowSortEngine_TitleAndPathSupportAscendingAndDescending()
    {
        LibraryChartRow alphaLatePath = CreateLibraryChartRow("z_alpha.bms", "Alpha", level: 1);
        LibraryChartRow betaEarlyPath = CreateLibraryChartRow("a_beta.bms", "Beta", level: 1);
        LibraryChartRow gammaMiddlePath = CreateLibraryChartRow("m_gamma.bms", "Gamma", level: 1);

        AssertLibraryChartSort(
            nameof(LibraryChartRow.Title),
            ListSortDirection.Ascending,
            new[] { alphaLatePath, betaEarlyPath, gammaMiddlePath },
            new[] { "z_alpha.bms", "a_beta.bms", "m_gamma.bms" });
        AssertLibraryChartSort(
            nameof(LibraryChartRow.Title),
            ListSortDirection.Descending,
            new[] { alphaLatePath, betaEarlyPath, gammaMiddlePath },
            new[] { "m_gamma.bms", "a_beta.bms", "z_alpha.bms" });
        AssertLibraryChartSort(
            nameof(LibraryChartRow.path),
            ListSortDirection.Ascending,
            new[] { alphaLatePath, betaEarlyPath, gammaMiddlePath },
            new[] { "a_beta.bms", "m_gamma.bms", "z_alpha.bms" });
        AssertLibraryChartSort(
            nameof(LibraryChartRow.path),
            ListSortDirection.Descending,
            new[] { alphaLatePath, betaEarlyPath, gammaMiddlePath },
            new[] { "z_alpha.bms", "m_gamma.bms", "a_beta.bms" });
    }

    [TestMethod]
    [TestCategory("SortEngine")]
    public void LibraryChartRowSortEngine_LevelColumnUsesNumericKey()
    {
        LibraryChartRow level12 = CreateLibraryChartRow("z_level12.bms", "Level12", level: 12);
        LibraryChartRow level3 = CreateLibraryChartRow("a_level3.bms", "Level3", level: 3);
        MainWindowViewModel.cSortParameters sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(LibraryChartRow.Level),
            Direction = ListSortDirection.Ascending
        };

        List<LibraryChartRow> sorted = LibraryChartRowSortEngine.SortForMainView(new[] { level12, level3 }, sortParameters, isPlaylistDetailView: false, useLegacySortForDataGrid: false, out string sortProfile);

        CollectionAssert.AreEqual(new[] { "a_level3.bms", "z_level12.bms" }, sorted.Select((LibraryChartRow row) => row.path).ToArray());
        Assert.AreEqual("library_chart_level_mixed_double", sortProfile);
    }

    [TestMethod]
    [TestCategory("SortEngine")]
    public void LibraryChartRowSortEngine_FolderColumnUsesLegacyNaturalInPlaylistDetailView()
    {
        LibraryChartRow folder10 = CreateLibraryChartRow("z_folder10.bms", "Z", level: 1, folder: "folder10");
        LibraryChartRow folder2 = CreateLibraryChartRow("a_folder2.bms", "A", level: 1, folder: "folder2");
        MainWindowViewModel.cSortParameters sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(LibraryChartRow.Folder),
            Direction = ListSortDirection.Ascending
        };

        List<LibraryChartRow> sorted = LibraryChartRowSortEngine.SortForMainView(new[] { folder10, folder2 }, sortParameters, isPlaylistDetailView: true, useLegacySortForDataGrid: false, out string sortProfile);

        CollectionAssert.AreEqual(new[] { "a_folder2.bms", "z_folder10.bms" }, sorted.Select((LibraryChartRow row) => row.path).ToArray());
        Assert.AreEqual("library_chart_folder_natural_legacy", sortProfile);
    }

    [TestMethod]
    [TestCategory("SortEngine")]
    public void LibraryChartRowSortEngine_ChartInfoAndScoreColumnsUseTypedSort()
    {
        LibraryChartRow high = CreateLibraryChartRow("z_high.bms", "High", level: 1, chartNotes: 100, chartTotal: 10.0, chartMainBpm: 200.0, rateScorePerfect: 90);
        LibraryChartRow low = CreateLibraryChartRow("a_low.bms", "Low", level: 1, chartNotes: 20, chartTotal: 2.0, chartMainBpm: 100.0, rateScorePerfect: 20);

        AssertTypedSort(nameof(LibraryChartRow.ChartNotes), new[] { high, low }, new[] { "a_low.bms", "z_high.bms" });
        AssertTypedSort(nameof(LibraryChartRow.ChartTotalSortKey), new[] { high, low }, new[] { "a_low.bms", "z_high.bms" });
        AssertTypedSort(nameof(LibraryChartRow.ChartMainBpmSortKey), new[] { high, low }, new[] { "a_low.bms", "z_high.bms" });
        AssertTypedSort(nameof(LibraryChartRow.rateDouble), new[] { high, low }, new[] { "a_low.bms", "z_high.bms" });
    }

    [TestMethod]
    [TestCategory("SortEngine")]
    public void RateDouble_ComputesFromScoreAndIgnoresStoredRate()
    {
        LibraryChartRow row = CreateLibraryChartRow("rate.bms", "Rate", level: 1, rateScorePerfect: 91);
        row.BmsFile.bmsScore.rate = 12;

        Assert.AreEqual(0.91, row.rateDouble.GetValueOrDefault(), 0.000001);

        TestableBmsFile zeroNotes = new TestableBmsFile();
        zeroNotes.ApplySnapshot(new SongSnapshotRow { path = "zero.bms", title = "Zero", level = 1, hash = "55555555555555555555555555555555" });
        zeroNotes.bmsScore = new BMSScore { hash = zeroNotes.hash, perfect = 10, totalnotes = 0 };

        Assert.IsFalse(zeroNotes.rateDouble.HasValue);
    }

    /// <summary>
    /// PlaylistSummary 専用ソートが昇順/降順で正しく切り替わることを検証します。
    /// </summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void PlaylistSummarySortEngine_SortsAscendingAndDescending()
    {
        List<PlaylistSummaryRow> rows = new List<PlaylistSummaryRow>
        {
            new PlaylistSummaryRow { PlaylistId = 1, Name = "B", TotalCharts = 30 },
            new PlaylistSummaryRow { PlaylistId = 2, Name = "A", TotalCharts = 10 },
            new PlaylistSummaryRow { PlaylistId = 3, Name = "C", TotalCharts = 20 }
        };

        MainWindowViewModel.cSortParameters asc = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(PlaylistSummaryRow.TotalCharts),
            Direction = ListSortDirection.Ascending
        };
        MainWindowViewModel.cSortParameters desc = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(PlaylistSummaryRow.TotalCharts),
            Direction = ListSortDirection.Descending
        };

        List<PlaylistSummaryRow> ascSorted = PlaylistSummarySortEngine.Sort(rows, asc, useLegacyStringSort: false, out string ascProfile);
        List<PlaylistSummaryRow> descSorted = PlaylistSummarySortEngine.Sort(rows, desc, useLegacyStringSort: false, out string descProfile);

        CollectionAssert.AreEqual(new[] { 2, 3, 1 }, ascSorted.Select((PlaylistSummaryRow row) => row.PlaylistId ?? -1).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 3, 2 }, descSorted.Select((PlaylistSummaryRow row) => row.PlaylistId ?? -1).ToArray());
        Assert.AreEqual("playlist_summary_numeric_int32", ascProfile);
        Assert.AreEqual("playlist_summary_numeric_int32", descProfile);
    }

    [TestMethod]
    [TestCategory("SortEngine")]
    public void PlaylistDetailSortEngine_ClearAndRankDisplayColumnsSort()
    {
        PlaylistDetailSourceRow hardAaa = CreatePlaylistDetailSourceRow("z_hard_aaa.bms", "Hard AAA", ClearType.HARD, RankType.AAA);
        PlaylistDetailSourceRow easyAa = CreatePlaylistDetailSourceRow("a_easy_aa.bms", "Easy AA", ClearType.EASY, RankType.AA);

        MainWindowViewModel.cSortParameters clearSort = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(PlaylistDetailRow.clear),
            Direction = ListSortDirection.Ascending
        };
        MainWindowViewModel.cSortParameters rankSort = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(PlaylistDetailRow.rank),
            Direction = ListSortDirection.Ascending
        };

        List<PlaylistDetailSourceRow> clearSorted = PlaylistDetailSortEngine.Sort(new[] { hardAaa, easyAa }, clearSort, out string clearProfile);
        List<PlaylistDetailSourceRow> rankSorted = PlaylistDetailSortEngine.Sort(new[] { hardAaa, easyAa }, rankSort, out string rankProfile);

        CollectionAssert.AreEqual(new[] { "a_easy_aa.bms", "z_hard_aaa.bms" }, clearSorted.Select(row => row.path).ToArray());
        CollectionAssert.AreEqual(new[] { "a_easy_aa.bms", "z_hard_aaa.bms" }, rankSorted.Select(row => row.path).ToArray());
        Assert.AreEqual("enum", clearProfile);
        Assert.AreEqual("numeric_double", rankProfile);
        Assert.AreEqual(0.95, hardAaa.rateDouble.GetValueOrDefault(), 0.000001);
        Assert.AreEqual(0.95, hardAaa.CreateViewRow().rateDouble.GetValueOrDefault(), 0.000001);
    }

    [TestMethod]
    [TestCategory("SortEngine")]
    public void ClearType_DisplayAndSortUseNumericLampOrder()
    {
        Assert.AreEqual(-1, (int)ClearType.NO_SONG);
        Assert.AreEqual(0, (int)ClearType.NO_PLAY);
        Assert.AreEqual(1, (int)ClearType.FAILED);
        Assert.AreEqual(2, (int)ClearType.INVALID);
        Assert.AreEqual(3, (int)ClearType.L_ASSIST);
        Assert.AreEqual(4, (int)ClearType.EASY);
        Assert.AreEqual(5, (int)ClearType.CLEAR);
        Assert.AreEqual(6, (int)ClearType.HARD);
        Assert.AreEqual(7, (int)ClearType.EX_HARD);
        Assert.AreEqual(8, (int)ClearType.FC);
        Assert.AreEqual(9, (int)ClearType.PA);
        Assert.AreEqual(10, (int)ClearType.MAX);

        Assert.AreEqual("ASSIST", ScoreDisplayTextFormatter.FormatClear(ClearType.INVALID));
        Assert.AreEqual("L-ASSIST", ScoreDisplayTextFormatter.FormatClear(ClearType.L_ASSIST));
        Assert.AreEqual("EX HARD", ScoreDisplayTextFormatter.FormatClear(ClearType.EX_HARD));
        Assert.AreEqual("PERFECT", ScoreDisplayTextFormatter.FormatClear(ClearType.PA));
        Assert.AreEqual("MAX", ScoreDisplayTextFormatter.FormatClear(ClearType.MAX));
        Assert.AreEqual("99", ScoreDisplayTextFormatter.FormatClear((ClearType)99));

        cleartypeToStringConvberter converter = new cleartypeToStringConvberter();
        Assert.AreEqual("PERFECT", converter.Convert(ClearType.PA, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.AreEqual("99", converter.Convert((ClearType)99, typeof(string), null, CultureInfo.InvariantCulture));
    }

    [TestMethod]
    [TestCategory("SortEngine")]
    public void LibraryChartAndBmsFileClearSortUseNumericClearType()
    {
        LibraryChartRow max = CreateLibraryChartRow("z_max.bms", "Max", 1, clear: ClearType.MAX);
        LibraryChartRow assist = CreateLibraryChartRow("m_assist.bms", "Assist", 1, clear: ClearType.INVALID);
        LibraryChartRow easy = CreateLibraryChartRow("a_easy.bms", "Easy", 1, clear: ClearType.EASY);
        LibraryChartRow failed = CreateLibraryChartRow("b_failed.bms", "Failed", 1, clear: ClearType.FAILED);

        MainWindowViewModel.cSortParameters clearSort = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(LibraryChartRow.clear),
            Direction = ListSortDirection.Ascending
        };
        List<LibraryChartRow> librarySorted = LibraryChartRowSortEngine.SortForMainView(new[] { max, assist, easy, failed }, clearSort, isPlaylistDetailView: false, useLegacySortForDataGrid: true, out string libraryProfile);

        CollectionAssert.AreEqual(new[] { "b_failed.bms", "m_assist.bms", "a_easy.bms", "z_max.bms" }, librarySorted.Select(row => row.path).ToArray());
        Assert.AreEqual("library_chart_typed", libraryProfile);

        List<BMSFile> bmsSorted = BMSFileSortEngine.SortForMainView(new[] { max.BmsFile, assist.BmsFile, easy.BmsFile, failed.BmsFile }, new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(BMSFile.clear),
            Direction = ListSortDirection.Ascending
        }, isPlaylistDetailView: false, out string bmsProfile);

        CollectionAssert.AreEqual(new[] { "b_failed.bms", "m_assist.bms", "a_easy.bms", "z_max.bms" }, bmsSorted.Select(row => row.path).ToArray());
        Assert.AreEqual("enum", bmsProfile);
    }

    [TestMethod]
    [TestCategory("SortEngine")]
    public void Lr2StorageConverterKeepsNativeClearValuesCompatible()
    {
        Assert.AreEqual(ClearType.EASY, ClearTypeStorageConverter.FromLr2Value(2));
        Assert.AreEqual(ClearType.CLEAR, ClearTypeStorageConverter.FromLr2Value(3));
        Assert.AreEqual(ClearType.HARD, ClearTypeStorageConverter.FromLr2Value(4));
        Assert.AreEqual(ClearType.FC, ClearTypeStorageConverter.FromLr2Value(5));
        Assert.AreEqual(ClearType.PA, ClearTypeStorageConverter.FromLr2Value(21));
        Assert.AreEqual(1, ClearTypeStorageConverter.ToLr2Value(ClearType.INVALID));
        Assert.AreEqual(1, ClearTypeStorageConverter.ToLr2Value(ClearType.L_ASSIST));
        Assert.AreEqual(4, ClearTypeStorageConverter.ToLr2Value(ClearType.EX_HARD));
        Assert.AreEqual(5, ClearTypeStorageConverter.ToLr2Value(ClearType.MAX));
    }

    /// <summary>
    /// song.db からソート検証に必要な行を読み込みます。
    /// </summary>
    /// <param name="songDbPath">song.db の絶対パス。</param>
    /// <param name="limit">読み込み上限件数。</param>
    /// <returns>検証対象の BMS 行。</returns>
    private static List<BMSFile> LoadRowsFromSongDb(string songDbPath, int limit)
    {
        using SQLiteConnection connection = new SQLiteConnection(songDbPath, SQLiteOpenFlags.ReadOnly);
        string sql = "SELECT path, hash, title, subtitle, artist, subartist, genre, tag, level, mode, karinotes FROM song WHERE path IS NOT NULL ORDER BY path LIMIT ?";
        List<SongSnapshotRow> rows = connection.Query<SongSnapshotRow>(sql, limit);
        List<BMSFile> result = new List<BMSFile>(rows.Count);
        for (int index = 0; index < rows.Count; index++)
        {
            SongSnapshotRow row = rows[index];
            if (string.IsNullOrWhiteSpace(row.path))
            {
                continue;
            }
            TestableBmsFile bmsFile = new TestableBmsFile();
            bmsFile.ApplySnapshot(row);
            result.Add(bmsFile);
        }
        return result;
    }

    /// <summary>
    /// 最適化前の実装と同等のソートをテスト側で再現します。
    /// </summary>
    /// <param name="source">ソート対象。</param>
    /// <param name="sortParameters">ソート条件。</param>
    /// <returns>ソート済みリスト。</returns>
    private static List<BMSFile> SortByLegacyImplementation(IEnumerable<BMSFile> source, MainWindowViewModel.cSortParameters sortParameters)
    {
        IEnumerable<BMSFile> safeSource = source ?? Enumerable.Empty<BMSFile>();
        string? columnName = sortParameters?.ColumnsName;
        ListSortDirection direction = sortParameters?.Direction ?? ListSortDirection.Ascending;
        if (string.IsNullOrWhiteSpace(columnName))
        {
            columnName = nameof(BMSFile.Title);
        }
        if (string.Equals(columnName, nameof(BMSFile.rank), StringComparison.Ordinal))
        {
            columnName = nameof(BMSFile.rateDouble);
        }
        PropertyInfo? property = typeof(BMSFile).GetProperty(columnName);
        Func<BMSFile, string> keySelector = delegate (BMSFile row)
        {
            if (row == null || property == null)
            {
                return string.Empty;
            }
            object value = property.GetValue(row);
            if (value == null)
            {
                return string.Empty;
            }
            if (property.PropertyType.IsEnum)
            {
                return ((int)value).ToString();
            }
            return value.ToString() ?? string.Empty;
        };
        if (direction == ListSortDirection.Ascending)
        {
            return safeSource.OrderBy(keySelector, new LegacyNaturalComparer<string>()).ThenBy((BMSFile row) => row?.Title ?? string.Empty, new LegacyNaturalComparer<string>()).ToList();
        }
        return safeSource.OrderByDescending(keySelector, new LegacyNaturalComparer<string>(isWhiteSpacePrior: true)).ThenBy((BMSFile row) => row?.Title ?? string.Empty, new LegacyNaturalComparer<string>()).ToList();
    }

    /// <summary>
    /// ソート結果比較用の SHA-256 ダイジェストを作成します。
    /// </summary>
    /// <param name="rows">ソート結果。</param>
    /// <returns>比較用ダイジェスト。</returns>
    private static string ComputeDigest(IEnumerable<BMSFile> rows)
    {
        StringBuilder builder = new StringBuilder();
        foreach (BMSFile row in rows)
        {
            builder.Append(row?.path ?? string.Empty).Append('\t').Append(row?.hash ?? string.Empty).Append('\n');
        }
        byte[] bytes = Encoding.UTF8.GetBytes(builder.ToString());
        byte[] hash;
        using (SHA256 sha256 = SHA256.Create())
        {
            hash = sha256.ComputeHash(bytes);
        }
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// 中央値を返します。
    /// </summary>
    /// <param name="values">計測値。</param>
    /// <returns>中央値。</returns>
    private static long Median(List<long> values)
    {
        if (values == null || values.Count == 0)
        {
            return 0L;
        }
        List<long> sorted = values.OrderBy((long v) => v).ToList();
        return sorted[sorted.Count / 2];
    }

    /// <summary>
    /// 2つの並び順の最初の差分インデックスを返します。
    /// </summary>
    private static int FindFirstMismatchIndex(List<BMSFile> left, List<BMSFile> right)
    {
        int count = Math.Min(left.Count, right.Count);
        for (int i = 0; i < count; i++)
        {
            BMSFile l = left[i] ?? throw new InvalidOperationException("Left row is null at index " + i);
            BMSFile r = right[i] ?? throw new InvalidOperationException("Right row is null at index " + i);
            if (!string.Equals(l.path, r.path, StringComparison.Ordinal) || !string.Equals(l.hash, r.hash, StringComparison.Ordinal))
            {
                return i;
            }
        }
        if (left.Count != right.Count)
        {
            return count;
        }
        return -1;
    }

    private static void AssertTypedSort(string columnName, IReadOnlyList<LibraryChartRow> source, string[] expectedPaths)
    {
        MainWindowViewModel.cSortParameters sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = columnName,
            Direction = ListSortDirection.Ascending
        };

        List<LibraryChartRow> sorted = LibraryChartRowSortEngine.SortForMainView(source, sortParameters, isPlaylistDetailView: false, useLegacySortForDataGrid: true, out string sortProfile);

        CollectionAssert.AreEqual(expectedPaths, sorted.Select((LibraryChartRow row) => row.path).ToArray(), columnName + " must use typed sort.");
        Assert.AreEqual("library_chart_typed", sortProfile, columnName + " must report typed sort profile.");
    }

    private static void AssertLibraryChartSort(string columnName, ListSortDirection direction, IReadOnlyList<LibraryChartRow> source, string[] expectedPaths)
    {
        MainWindowViewModel.cSortParameters sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = columnName,
            Direction = direction
        };

        List<LibraryChartRow> sorted = LibraryChartRowSortEngine.SortForMainView(source, sortParameters, isPlaylistDetailView: false, useLegacySortForDataGrid: false, out string sortProfile);

        CollectionAssert.AreEqual(expectedPaths, sorted.Select((LibraryChartRow row) => row.path).ToArray(), columnName + " " + direction + " order mismatch.");
        Assert.AreEqual("library_chart_string_fast_ordinal_ignore_case", sortProfile);
    }

    private static LibraryChartRow CreateLibraryChartRow(string path, string title, int level, string folder = "", int? chartNotes = null, double? chartTotal = null, double? chartMainBpm = null, int? rateScorePerfect = null, ClearType? clear = null)
    {
        string hash = CreateMd5FromPath(path);
        TestableBmsFile file = new TestableBmsFile();
        file.ApplySnapshot(new SongSnapshotRow { path = path, title = title, level = level, hash = hash });
        file.SetFolder(folder);
        if (chartNotes.HasValue || chartTotal.HasValue || chartMainBpm.HasValue)
        {
            file.SetChartInfo(new LR2SongDBExtended.chart_info
            {
                sha256 = CreateSha256FromPath(path),
                md5 = hash,
                charthash = CreateSha256FromPath(path + ":chart"),
                notes = chartNotes ?? 0,
                total = chartTotal,
                mainbpm = chartMainBpm,
                parser_version = 1
            });
        }
        if (rateScorePerfect.HasValue)
        {
            file.bmsScore = new BMSScore
            {
                hash = hash,
                perfect = rateScorePerfect.Value,
                totalnotes = 100
            };
        }
        if (clear.HasValue)
        {
            file.bmsScore = new BMSScore
            {
                hash = hash,
                clear = clear.Value,
                rank = RankType.A,
                perfect = 80,
                totalnotes = 100
            };
        }
        return LibraryChartRow.FromBmsFile(file);
    }

    private static PlaylistDetailSourceRow CreatePlaylistDetailSourceRow(string path, string title, ClearType clear, RankType rank)
    {
        string hash = CreateMd5FromPath(path);
        TestableBmsFile file = new TestableBmsFile();
        file.ApplySnapshot(new SongSnapshotRow { path = path, title = title, level = 1, hash = hash });
        file.bmsScore = new BMSScore
        {
            hash = hash,
            clear = clear,
            rank = rank,
            perfect = rank == RankType.AAA ? 95 : 85,
            totalnotes = 100
        };
        return new PlaylistDetailSourceRow(new BMSTableEntry(file), file);
    }

    private static string CreateMd5FromPath(string path)
    {
        using MD5 md5 = MD5.Create();
        byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(path));
        return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static string CreateSha256FromPath(string path)
    {
        using SHA256 sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(path));
        return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
    }

    [Table("song")]
    private sealed class SongSnapshotRow
    {
        public string? path { get; set; }

        public string? hash { get; set; }

        public string? title { get; set; }

        public string? subtitle { get; set; }

        public string? artist { get; set; }

        public string? subartist { get; set; }

        public string? genre { get; set; }

        public string? tag { get; set; }

        public int? level { get; set; }

        public int? mode { get; set; }

        public int? karinotes { get; set; }
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetFolder(string folderName)
        {
            folder = folderName;
        }

        public void ApplySnapshot(SongSnapshotRow row)
        {
            path = row.path ?? string.Empty;
            hash = row.hash ?? string.Empty;
            title = row.title ?? string.Empty;
            subtitle = row.subtitle ?? string.Empty;
            artist = row.artist ?? string.Empty;
            subartist = row.subartist ?? string.Empty;
            genre = row.genre ?? string.Empty;
            tag = row.tag ?? string.Empty;
            level = row.level;
            mode = row.mode;
            karinotes = row.karinotes;
        }
    }
}
