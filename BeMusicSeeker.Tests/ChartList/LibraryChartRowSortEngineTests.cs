using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 通常一覧とplaylistの比較規則、部分更新の分岐、スコア表示と外部保存値の互換性を検証します。
/// </summary>
[TestClass]
public sealed class LibraryChartRowSortEngineTests
{
    /// <summary>LEVELの12と3を数値の昇順へ並べ、表示文字列の辞書順で逆転する誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void Sort_LevelColumn_UsesNumericKeyForRegularRows()
    {
        LibraryChartRow regularLevel12 = CreateLibraryChartRow("z_regular_12.bms", "Regular12", level: 12);
        LibraryChartRow regularLevel3 = CreateLibraryChartRow("m_regular_3.bms", "Regular3", level: 3);
        List<LibraryChartRow> source = [regularLevel12, regularLevel3];
        var sortParameters = new ChartListSortParameters
        {
            ColumnsName = nameof(LibraryChartRow.Level),
            Direction = ListSortDirection.Ascending
        };

        List<LibraryChartRow> sorted = LibraryChartRowSortEngine.SortForMainView(source, ToSortSpecification(sortParameters), isPlaylistDetailView: false, out _);
        string[] sortedPaths = [.. sorted.Select(row => row.path)];

        CollectionAssert.AreEqual(
            new[] { "m_regular_3.bms", "z_regular_12.bms" },
            sortedPaths,
            "LEVEL must be sorted numerically instead of lexicographically.");

    }

    /// <summary>通常folderの10と2は文字列順に並べ、playlist用の自然順を通常一覧へ適用しません。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void Sort_FolderColumn_UsesStringOrderInRegularView()
    {
        LibraryChartRow folder10 = CreateLibraryChartRow("z.bms", "Z", level: 1, folder: "folder10");
        LibraryChartRow folder2 = CreateLibraryChartRow("a.bms", "A", level: 1, folder: "folder2");

        var sortParameters = new ChartListSortParameters
        {
            ColumnsName = nameof(LibraryChartRow.Folder),
            Direction = ListSortDirection.Ascending
        };

        List<LibraryChartRow> sorted = LibraryChartRowSortEngine.SortForMainView([folder10, folder2], ToSortSpecification(sortParameters), isPlaylistDetailView: false, out _);

        CollectionAssert.AreEqual(new[] { "z.bms", "a.bms" }, sorted.Select(row => row.path).ToArray());

    }

    /// <summary>playlistのfolder10とfolder2は従来の自然順へ並べ、通常一覧用の文字列順へ変えません。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void Sort_FolderColumn_PreservesNaturalOrderInPlaylistDetailView()
    {
        LibraryChartRow folder10 = CreateLibraryChartRow("z.bms", "Z", level: 1, folder: "folder10");
        LibraryChartRow folder2 = CreateLibraryChartRow("a.bms", "A", level: 1, folder: "folder2");

        var sortParameters = new ChartListSortParameters
        {
            ColumnsName = nameof(LibraryChartRow.Folder),
            Direction = ListSortDirection.Ascending
        };

        List<LibraryChartRow> sorted = LibraryChartRowSortEngine.SortForMainView([folder10, folder2], ToSortSpecification(sortParameters), isPlaylistDetailView: true, out _);

        CollectionAssert.AreEqual(new[] { "a.bms", "z.bms" }, sorted.Select(row => row.path).ToArray());

    }

    /// <summary>異なる題名をAAA、bbbの順へ並べ、入力順やパス順に依存しません。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void Sort_TitleColumn_UsesTitleOrder()
    {
        LibraryChartRow row1 = CreateLibraryChartRow("b.bms", "bbb", level: 1);
        LibraryChartRow row2 = CreateLibraryChartRow("a.bms", "AAA", level: 1);

        var sortParameters = new ChartListSortParameters
        {
            ColumnsName = nameof(LibraryChartRow.Title),
            Direction = ListSortDirection.Ascending
        };

        List<LibraryChartRow> sorted = LibraryChartRowSortEngine.SortForMainView([row1, row2], ToSortSpecification(sortParameters), isPlaylistDetailView: false, out _);

        CollectionAssert.AreEqual(new[] { "a.bms", "b.bms" }, sorted.Select(row => row.path).ToArray());

    }

    /// <summary>題名item10とitem2を文字列順へ並べ、実sortの診断に対象件数・列・方向と非負の所要時間を保持します。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void LibraryChartRowSortEngine_StringSortMetricsDescribeObservedWorkload()
    {
        LibraryChartRow title10 = CreateLibraryChartRow("z_item10.bms", "item10", level: 1);
        LibraryChartRow title2 = CreateLibraryChartRow("a_item2.bms", "item2", level: 1);
        var sortParameters = new ChartListSortParameters
        {
            ColumnsName = nameof(LibraryChartRow.Title),
            Direction = ListSortDirection.Ascending
        };

        List<LibraryChartRow> fastSorted = LibraryChartRowSortEngine.SortForMainView([title10, title2], ToSortSpecification(sortParameters), isPlaylistDetailView: false, out _, out LibraryChartSortMetrics metrics);

        CollectionAssert.AreEqual(new[] { "z_item10.bms", "a_item2.bms" }, fastSorted.Select(row => row.path).ToArray());

        Assert.AreEqual(2, metrics.RowCount);
        Assert.AreEqual(nameof(LibraryChartRow.Title), metrics.ColumnName);
        Assert.AreEqual(ListSortDirection.Ascending, metrics.Direction);

        Assert.IsTrue(metrics.SortMs >= 0);
    }

    /// <summary>通常・全走査と部分絞込み/sortを仮想一覧対象とし、playlist・保守・導入区分を誤って通常仮想処理へ通しません。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void VirtualNormalLibraryModeSupport_CoversNormalListRefreshAndFilterUpdates()
    {
        MainViewUpdateMode[] supportedModes =
        [
            MainViewUpdateMode.TreeViewFilterNotChanged,
            MainViewUpdateMode.FolderFilterSelected,
            MainViewUpdateMode.FullScanAllChartsFilterSelected,
            MainViewUpdateMode.KeywordFilterUpdated,
            MainViewUpdateMode.ModeFilterUpdated,
            MainViewUpdateMode.SortUpdated
        ];

        foreach (MainViewUpdateMode mode in Enum.GetValues(typeof(MainViewUpdateMode)).Cast<MainViewUpdateMode>())
        {
            Assert.AreEqual(
                supportedModes.Contains(mode),
                MainViewRefreshDecisionService.IsVirtualNormalLibraryModeSupported(mode),
                mode.ToString());
        }
    }

    /// <summary>ライブラリが欠損した通常folder更新は欠損経路を優先し、playlistやbmson一覧処理を始めません。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void ChartListRefreshCoordinator_RoutesMissingFilesBeforeViewPipelines()
    {
        ChartListRefreshRoute route = ChartListRefreshCoordinator.ResolveRoute(
            MainViewUpdateMode.FolderFilterSelected,
            MainViewUpdateMode.FolderFilterSelected,
            MainViewUpdateMode.FolderFilterSelected,
            hasFiles: false);

        Assert.AreEqual(ChartListRefreshRouteKind.MissingFiles, route.Kind);
        Assert.IsFalse(route.IsPlaylistTreeActive);
        Assert.IsFalse(route.IncludeBmsonRows);
    }

    /// <summary>履歴選択中のsortは履歴更新へ配送し、playlist構築や通常bmson追加へ混入しません。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void ChartListRefreshCoordinator_RoutesPlayHistoryIncrementalUpdatesToPlayHistoryView()
    {
        ChartListRefreshRoute route = ChartListRefreshCoordinator.ResolveRoute(
            MainViewUpdateMode.SortUpdated,
            MainViewUpdateMode.SortUpdated,
            MainViewUpdateMode.PlayHistorySelected,
            hasFiles: true);

        Assert.AreEqual(ChartListRefreshRouteKind.ApplyPlayHistoryView, route.Kind);
        Assert.IsFalse(route.IsPlaylistTreeActive);
        Assert.IsFalse(route.IncludeBmsonRows);
    }

    /// <summary>playlist選択中のキーワード変更は表sourceの構築へ配送し、通常bmson一覧の更新へ混入しません。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void ChartListRefreshCoordinator_RoutesPlaylistIncrementalUpdatesToPlaylistBuild()
    {
        ChartListRefreshRoute route = ChartListRefreshCoordinator.ResolveRoute(
            MainViewUpdateMode.KeywordFilterUpdated,
            MainViewUpdateMode.KeywordFilterUpdated,
            MainViewUpdateMode.PlaylistFilterSelected,
            hasFiles: true);

        Assert.AreEqual(ChartListRefreshRouteKind.RegisterPlaylistSourceBuild, route.Kind);
        Assert.IsTrue(route.IsPlaylistTreeActive);
        Assert.IsFalse(route.IncludeBmsonRows);
    }

    /// <summary>ライブラリがある通常folder更新は通常処理へ継続し、bmsonを対象から欠落させません。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void ChartListRefreshCoordinator_RoutesNormalLibraryWithBmsonRows()
    {
        ChartListRefreshRoute route = ChartListRefreshCoordinator.ResolveRoute(
            MainViewUpdateMode.FolderFilterSelected,
            MainViewUpdateMode.FolderFilterSelected,
            MainViewUpdateMode.FolderFilterSelected,
            hasFiles: true);

        Assert.AreEqual(ChartListRefreshRouteKind.ContinueMainLibrary, route.Kind);
        Assert.IsFalse(route.IsPlaylistTreeActive);
        Assert.IsTrue(route.IncludeBmsonRows);
    }

    /// <summary>playlist・保守・導入区分では通常bmson追加を避け、通常と全走査および未知区分では従来どおり含める条件を全modeで確認します。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void ChartListRefreshCoordinator_ExcludesBmsonRowsForMaintenanceAndInstallViews()
    {
        var excludedModes = new HashSet<MainViewUpdateMode>
        {
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistNotOwnedFilterSelected,
            MainViewUpdateMode.FileMissingFilterSelected,
            MainViewUpdateMode.FileMissingIgnoredFilterSelected,
            MainViewUpdateMode.DuplicateFilterSelected,
            MainViewUpdateMode.GarbledFilterSelected,
            MainViewUpdateMode.GarbleFixedFilterSelected,
            MainViewUpdateMode.UnregisteredFilterSelected,
            MainViewUpdateMode.ZeroNoteFilterSelected,
            MainViewUpdateMode.ChartInfoParseErrorFilterSelected,
            MainViewUpdateMode.NewlyInstalledFolderSelected,
            MainViewUpdateMode.PendingInstallFolderSelected
        };

        foreach (MainViewUpdateMode mode in (MainViewUpdateMode[])Enum.GetValues(typeof(MainViewUpdateMode)))
        {
            bool expected = mode == MainViewUpdateMode.FullScanAllChartsFilterSelected
                || !excludedModes.Contains(mode);
            Assert.AreEqual(
                expected,
                ChartListRefreshCoordinator.ShouldIncludeBmsonLibraryRows(mode, mode),
                mode.ToString());
        }

        Assert.IsTrue(ChartListRefreshCoordinator.ShouldIncludeBmsonLibraryRows((MainViewUpdateMode)999, (MainViewUpdateMode)999));
    }

    /// <summary>仮想比較に対応する各列と未指定をcache対象にし、未知のPath表記を拒否して未対応比較をcacheへ通しません。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void NormalLibrarySortCacheCandidate_AllowsVirtualRegistryColumns()
    {
        Assert.IsTrue(RegularChartListOwner.IsSortCacheCandidate(null));
        Assert.IsTrue(RegularChartListOwner.IsSortCacheCandidate(string.Empty));
        Assert.IsTrue(RegularChartListOwner.IsSortCacheCandidate(nameof(LibraryChartRow.Title)));
        Assert.IsTrue(RegularChartListOwner.IsSortCacheCandidate(nameof(LibraryChartRow.path)));
        Assert.IsTrue(RegularChartListOwner.IsSortCacheCandidate(nameof(LibraryChartRow.Folder)));
        Assert.IsTrue(RegularChartListOwner.IsSortCacheCandidate(nameof(LibraryChartRow.Artist)));
        Assert.IsTrue(RegularChartListOwner.IsSortCacheCandidate(nameof(LibraryChartRow.genre)));
        Assert.IsTrue(RegularChartListOwner.IsSortCacheCandidate(nameof(LibraryChartRow.mode)));
        Assert.IsTrue(RegularChartListOwner.IsSortCacheCandidate(nameof(LibraryChartRow.tag)));
        Assert.IsTrue(RegularChartListOwner.IsSortCacheCandidate(nameof(LibraryChartRow.hash)));
        Assert.IsTrue(RegularChartListOwner.IsSortCacheCandidate(nameof(LibraryChartRow.sha256)));
        Assert.IsFalse(RegularChartListOwner.IsSortCacheCandidate("Path"));
        Assert.IsTrue(RegularChartListOwner.IsSortCacheCandidate(nameof(LibraryChartRow.instl_dst)));
        Assert.IsTrue(RegularChartListOwner.IsSortCacheCandidate(nameof(LibraryChartRow.InstallDestinationTitle)));
        Assert.IsTrue(RegularChartListOwner.IsSortCacheCandidate(nameof(LibraryChartRow.InstallDestinationArtist)));
        Assert.IsTrue(RegularChartListOwner.IsSortCacheCandidate(nameof(LibraryChartRow.RefTablesSymbols)));
        Assert.IsTrue(RegularChartListOwner.IsSortCacheCandidate(nameof(LibraryChartRow.clear)));
        Assert.IsTrue(RegularChartListOwner.IsSortCacheCandidate(nameof(LibraryChartRow.rateDouble)));
        Assert.IsTrue(RegularChartListOwner.IsSortCacheCandidate(nameof(LibraryChartRow.score)));
        Assert.IsTrue(RegularChartListOwner.IsSortCacheCandidate(nameof(LibraryChartRow.maxcombo)));
        Assert.IsTrue(RegularChartListOwner.IsSortCacheCandidate(nameof(LibraryChartRow.minbp)));
        Assert.IsTrue(RegularChartListOwner.IsSortCacheCandidate(nameof(LibraryChartRow.ChartLevelSortKey)));
        Assert.IsTrue(RegularChartListOwner.IsSortCacheCandidate(nameof(LibraryChartRow.ChartTotalSortKey)));
        Assert.IsTrue(RegularChartListOwner.IsSortCacheCandidate(nameof(LibraryChartRow.WarningDigestText)));
        Assert.IsTrue(RegularChartListOwner.IsSortCacheCandidate(nameof(LibraryChartRow.WAVHealth)));
        Assert.IsTrue(RegularChartListOwner.IsSortCacheCandidate(nameof(LibraryChartRow.BGAHealth)));
        Assert.IsTrue(RegularChartListOwner.IsSortCacheCandidate(nameof(LibraryChartRow.MovieHealth)));
        Assert.IsTrue(RegularChartListOwner.IsSortCacheCandidate(nameof(LibraryChartRow.encoding)));
    }

    /// <summary>各列の変更依存をregistryと更新判断へ共有し、導入先・参照表・スコア・譜面情報・保守・警告の更新漏れを防ぎます。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void MainViewSortColumnDependency_ClassifiesMainColumnFamilies()
    {
        foreach (ChartListOrderColumnMetadata column in ChartListOrder.GetVirtualSortColumnMetadata())
        {
            Assert.AreEqual(column.Dependency, MainViewRefreshDecisionService.GetSortColumnDependency(column.NormalizedColumnName), column.NormalizedColumnName);
        }
        Assert.AreEqual(MainViewDataDependency.IdentitySortKey, MainViewRefreshDecisionService.GetSortColumnDependency(null));
        Assert.AreEqual(MainViewDataDependency.InstallDestination, MainViewRefreshDecisionService.GetSortColumnDependency(nameof(LibraryChartRow.instl_dst)));
        Assert.AreEqual(MainViewDataDependency.InstallDestination, MainViewRefreshDecisionService.GetSortColumnDependency(nameof(LibraryChartRow.InstallDestinationTitle)));
        Assert.AreEqual(MainViewDataDependency.InstallDestination, MainViewRefreshDecisionService.GetSortColumnDependency(nameof(LibraryChartRow.InstallDestinationArtist)));
        Assert.AreEqual(MainViewDataDependency.ReferenceTables, MainViewRefreshDecisionService.GetSortColumnDependency(nameof(LibraryChartRow.RefTablesSymbols)));
        Assert.AreEqual(MainViewDataDependency.Score, MainViewRefreshDecisionService.GetSortColumnDependency(nameof(LibraryChartRow.rateDouble)));
        Assert.AreEqual(MainViewDataDependency.Score, MainViewRefreshDecisionService.GetSortColumnDependency(nameof(LibraryChartRow.rankingString)));
        Assert.AreEqual(MainViewDataDependency.ChartInfo, MainViewRefreshDecisionService.GetSortColumnDependency(nameof(LibraryChartRow.ChartTotalSortKey)));
        Assert.AreEqual(MainViewDataDependency.Maintenance, MainViewRefreshDecisionService.GetSortColumnDependency(nameof(LibraryChartRow.WAVHealth)));
        Assert.AreEqual(MainViewDataDependency.Warning, MainViewRefreshDecisionService.GetSortColumnDependency(nameof(LibraryChartRow.WarningDigestText)));
    }

    /// <summary>題名・パス・source同一性の変更だけでsource cacheを捨て、警告や補完だけで不要な全件構築を増やしません。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void NormalLibrarySortKeyInvalidationReasons_ClearSourceRowsOnlyForSourceIdentityChanges()
    {
        Assert.IsTrue(MainViewRefreshDecisionService.ShouldClearSourceRowsForSortKeyChange("bms_title_changed"));
        Assert.IsTrue(MainViewRefreshDecisionService.ShouldClearSourceRowsForSortKeyChange("bms_path_changed"));
        Assert.IsTrue(MainViewRefreshDecisionService.ShouldClearSourceRowsForSortKeyChange("bmson_path_changed"));
        Assert.IsTrue(MainViewRefreshDecisionService.ShouldClearSourceRowsForSortKeyChange("bmson_source_identity_changed"));

        Assert.IsFalse(MainViewRefreshDecisionService.ShouldClearSourceRowsForSortKeyChange("bmson_sort_key_changed"));
        Assert.IsFalse(MainViewRefreshDecisionService.ShouldClearSourceRowsForSortKeyChange("maintenance_changed"));
        Assert.IsFalse(MainViewRefreshDecisionService.ShouldClearSourceRowsForSortKeyChange("warning_changed"));
        Assert.IsFalse(MainViewRefreshDecisionService.ShouldClearSourceRowsForSortKeyChange("chart_info_digest_backfilled"));
        Assert.IsFalse(MainViewRefreshDecisionService.ShouldClearSourceRowsForSortKeyChange("ref_tables_changed"));
    }

    /// <summary>BMSとbmsonのパス変更の有無を独立に無効化理由へ反映し、片方の変更を落としたり未変更を追加しません。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void NormalLibraryPathSortKeyInvalidationReasons_DistinguishBmsAndBmsonPathMutations()
    {
        CollectionAssert.AreEqual(
            new[] { "bms_path_changed" },
            MainViewRefreshDecisionService.GetPathSortKeyInvalidationReasons(hasBmsPathMutation: true, hasBmsonPathMutation: false).ToArray());
        CollectionAssert.AreEqual(
            new[] { "bmson_path_changed" },
            MainViewRefreshDecisionService.GetPathSortKeyInvalidationReasons(hasBmsPathMutation: false, hasBmsonPathMutation: true).ToArray());
        CollectionAssert.AreEqual(
            new[] { "bms_path_changed", "bmson_path_changed" },
            MainViewRefreshDecisionService.GetPathSortKeyInvalidationReasons(hasBmsPathMutation: true, hasBmsonPathMutation: true).ToArray());
        Assert.AreEqual(0, MainViewRefreshDecisionService.GetPathSortKeyInvalidationReasons(hasBmsPathMutation: false, hasBmsonPathMutation: false).Count);
    }

    /// <summary>絞込みのない通常一覧で題名sortに影響しないスコア・譜面情報・保守・警告変更は表示だけを更新し、不要な並べ替えを避けます。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void MainViewRefreshDecision_UsesDisplayRefreshWhenFullNormalLibrarySortIsUnaffected()
    {
        foreach (MainViewDataDependency dependency in new[]
        {
            MainViewDataDependency.Score,
            MainViewDataDependency.ChartInfo,
            MainViewDataDependency.Maintenance,
            MainViewDataDependency.Warning
        })
        {
            MainViewRefreshDecision decision = MainViewRefreshDecisionService.Build(
                MainViewUpdateMode.FolderFilterSelected,
                folderFilterApplied: false,
                keywordFilter: string.Empty,
                modeFilter: ChartModeFilter.All,
                sortColumnName: nameof(LibraryChartRow.Title),
                isPlaylistDetailView: false,
                dependency: dependency,
                reason: "hydration_completed");

            Assert.AreEqual(MainViewRefreshAction.RefreshDisplay, decision.Action, dependency.ToString());
            Assert.AreEqual(dependency, decision.Dependency, dependency.ToString());
            Assert.AreEqual(MainViewDataDependency.IdentitySortKey, decision.SortDependency, dependency.ToString());
        }
    }

    /// <summary>スコア列sort・スコア検索・未知sortの変更は一覧を再計算し、スコア更新後の行順や所属を古いまま保ちません。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void MainViewRefreshDecision_RefreshesWhenScoreUpdateCanAffectCurrentView()
    {
        MainViewRefreshDecision scoreSortDecision = MainViewRefreshDecisionService.Build(
            MainViewUpdateMode.FolderFilterSelected,
            folderFilterApplied: false,
            keywordFilter: string.Empty,
            modeFilter: ChartModeFilter.All,
            sortColumnName: nameof(LibraryChartRow.rateDouble),
            isPlaylistDetailView: false,
            dependency: MainViewDataDependency.Score,
            reason: "ranking_refresh_completed");
        MainViewRefreshDecision keywordDecision = MainViewRefreshDecisionService.Build(
            MainViewUpdateMode.FolderFilterSelected,
            folderFilterApplied: false,
            keywordFilter: "rate:>90",
            modeFilter: ChartModeFilter.All,
            sortColumnName: nameof(LibraryChartRow.Title),
            isPlaylistDetailView: false,
            dependency: MainViewDataDependency.Score,
            reason: "score_hydration_completed");
        MainViewRefreshDecision unknownSortDecision = MainViewRefreshDecisionService.Build(
            MainViewUpdateMode.FolderFilterSelected,
            folderFilterApplied: false,
            keywordFilter: string.Empty,
            modeFilter: ChartModeFilter.All,
            sortColumnName: "UnknownComputedColumn",
            isPlaylistDetailView: false,
            dependency: MainViewDataDependency.Score,
            reason: "score_hydration_completed");

        Assert.AreEqual(MainViewRefreshAction.Refresh, scoreSortDecision.Action);
        Assert.AreEqual(MainViewRefreshAction.Refresh, keywordDecision.Action);
        Assert.AreEqual(MainViewRefreshAction.Refresh, unknownSortDecision.Action);
    }

    /// <summary>通常と全走査で導入先変更が題名sortに無関係なら表示だけを更新し、導入先または警告sortなら順序を再計算します。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void MainViewRefreshDecision_RefreshesWhenInstallDestinationUpdateCanAffectCurrentView()
    {
        MainViewRefreshDecision titleSortDecision = MainViewRefreshDecisionService.Build(
            MainViewUpdateMode.FolderFilterSelected,
            folderFilterApplied: false,
            keywordFilter: string.Empty,
            modeFilter: ChartModeFilter.All,
            sortColumnName: nameof(LibraryChartRow.Title),
            isPlaylistDetailView: false,
            dependency: MainViewDataDependency.InstallDestination,
            reason: "normal_library_install_destination_changed");
        MainViewRefreshDecision installDestinationSortDecision = MainViewRefreshDecisionService.Build(
            MainViewUpdateMode.FolderFilterSelected,
            folderFilterApplied: false,
            keywordFilter: string.Empty,
            modeFilter: ChartModeFilter.All,
            sortColumnName: nameof(LibraryChartRow.instl_dst),
            isPlaylistDetailView: false,
            dependency: MainViewDataDependency.InstallDestination,
            reason: "normal_library_install_destination_changed");
        MainViewRefreshDecision warningSortDecision = MainViewRefreshDecisionService.Build(
            MainViewUpdateMode.FolderFilterSelected,
            folderFilterApplied: false,
            keywordFilter: string.Empty,
            modeFilter: ChartModeFilter.All,
            sortColumnName: nameof(LibraryChartRow.WarningDigestText),
            isPlaylistDetailView: false,
            dependency: MainViewDataDependency.InstallDestination,
            reason: "normal_library_install_destination_changed");
        MainViewRefreshDecision fullScanTitleSortDecision = MainViewRefreshDecisionService.Build(
            MainViewUpdateMode.FullScanAllChartsFilterSelected,
            folderFilterApplied: false,
            keywordFilter: string.Empty,
            modeFilter: ChartModeFilter.All,
            sortColumnName: nameof(LibraryChartRow.Title),
            isPlaylistDetailView: false,
            dependency: MainViewDataDependency.InstallDestination,
            reason: "normal_library_install_destination_changed");
        MainViewRefreshDecision fullScanWarningSortDecision = MainViewRefreshDecisionService.Build(
            MainViewUpdateMode.FullScanAllChartsFilterSelected,
            folderFilterApplied: false,
            keywordFilter: string.Empty,
            modeFilter: ChartModeFilter.All,
            sortColumnName: nameof(LibraryChartRow.WarningDigestText),
            isPlaylistDetailView: false,
            dependency: MainViewDataDependency.InstallDestination,
            reason: "normal_library_install_destination_changed");

        Assert.AreEqual(MainViewRefreshAction.RefreshDisplay, titleSortDecision.Action);
        Assert.AreEqual(MainViewRefreshAction.RefreshDisplay, fullScanTitleSortDecision.Action);
        Assert.AreEqual(MainViewRefreshAction.Refresh, installDestinationSortDecision.Action);
        Assert.AreEqual(MainViewDataDependency.InstallDestination, installDestinationSortDecision.SortDependency);
        Assert.AreEqual(MainViewRefreshAction.Refresh, warningSortDecision.Action);
        Assert.AreEqual(MainViewDataDependency.Warning, warningSortDecision.SortDependency);
        Assert.AreEqual(MainViewRefreshAction.Refresh, fullScanWarningSortDecision.Action);
        Assert.AreEqual(MainViewDataDependency.Warning, fullScanWarningSortDecision.SortDependency);
    }

    /// <summary>スコア・譜面情報・保守・警告の変更が現在のsort列と一致すると一覧を再計算し、表示更新だけで古い順を残しません。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void MainViewRefreshDecision_RefreshesWhenDependencyMatchesCurrentSort()
    {
        // スコア率、TOTAL、WAV健全性、警告の各変更と、対応する現在sortの組合せです。
        (MainViewDataDependency dependency, string sortColumnName)[] cases =
        [
            (MainViewDataDependency.Score, nameof(LibraryChartRow.rateDouble)),
            (MainViewDataDependency.ChartInfo, nameof(LibraryChartRow.ChartTotalSortKey)),
            (MainViewDataDependency.Maintenance, nameof(LibraryChartRow.WAVHealth)),
            (MainViewDataDependency.Warning, nameof(LibraryChartRow.WarningDigestText))
        ];

        foreach ((MainViewDataDependency dependency, string sortColumnName) in cases)
        {
            MainViewRefreshDecision decision = MainViewRefreshDecisionService.Build(
                MainViewUpdateMode.FolderFilterSelected,
                folderFilterApplied: false,
                keywordFilter: string.Empty,
                modeFilter: ChartModeFilter.All,
                sortColumnName: sortColumnName,
                isPlaylistDetailView: false,
                dependency: dependency,
                reason: "dependency_changed");

            Assert.AreEqual(MainViewRefreshAction.Refresh, decision.Action, dependency.ToString());
            Assert.AreEqual(dependency, decision.Dependency, dependency.ToString());
            Assert.AreEqual(dependency, decision.SortDependency, dependency.ToString());
        }
    }

    /// <summary>重複部分集合では所属とfolder順に無関係な警告変更を表示だけへ反映し、警告sortのときは順序を再計算します。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void MainViewRefreshDecision_UsesDisplayRefreshForDuplicateSubsetWhenMembershipIsUnaffected()
    {
        MainViewRefreshDecision warningDecision = MainViewRefreshDecisionService.Build(
            MainViewUpdateMode.DuplicateFilterSelected,
            folderFilterApplied: false,
            keywordFilter: string.Empty,
            modeFilter: ChartModeFilter.All,
            sortColumnName: nameof(LibraryChartRow.Folder),
            isPlaylistDetailView: false,
            dependency: MainViewDataDependency.Warning,
            reason: "chart_files_need_resource_fix_changed");

        Assert.AreEqual(MainViewRefreshAction.RefreshDisplay, warningDecision.Action);

        MainViewRefreshDecision warningSortDecision = MainViewRefreshDecisionService.Build(
            MainViewUpdateMode.DuplicateFilterSelected,
            folderFilterApplied: false,
            keywordFilter: string.Empty,
            modeFilter: ChartModeFilter.All,
            sortColumnName: nameof(LibraryChartRow.WarningDigestText),
            isPlaylistDetailView: false,
            dependency: MainViewDataDependency.Warning,
            reason: "chart_files_need_resource_fix_changed");

        Assert.AreEqual(MainViewRefreshAction.Refresh, warningSortDecision.Action);
    }

    /// <summary>重複一覧の警告検索またはsource所属が変わると一覧を再計算し、除外・追加された対象を表示だけで残しません。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void MainViewRefreshDecision_RefreshesDuplicateSubsetWhenFiltersCanDependOnChangedData()
    {
        MainViewRefreshDecision keywordDecision = MainViewRefreshDecisionService.Build(
            MainViewUpdateMode.DuplicateFilterSelected,
            folderFilterApplied: false,
            keywordFilter: "warning",
            modeFilter: ChartModeFilter.All,
            sortColumnName: nameof(LibraryChartRow.Folder),
            isPlaylistDetailView: false,
            dependency: MainViewDataDependency.Warning,
            reason: "chart_files_need_resource_fix_changed");
        MainViewRefreshDecision membershipDecision = MainViewRefreshDecisionService.Build(
            MainViewUpdateMode.DuplicateFilterSelected,
            folderFilterApplied: false,
            keywordFilter: string.Empty,
            modeFilter: ChartModeFilter.All,
            sortColumnName: nameof(LibraryChartRow.Folder),
            isPlaylistDetailView: false,
            dependency: MainViewDataDependency.SourceMembership,
            reason: "bms_files_duplicated_changed");

        Assert.AreEqual(MainViewRefreshAction.Refresh, keywordDecision.Action);
        Assert.AreEqual(MainViewRefreshAction.Refresh, membershipDecision.Action);
    }

    /// <summary>依存不明の変更を通常一覧の再計算へ通し、影響しないと決めつけて表示だけへ縮めません。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void MainViewRefreshDecision_RefreshesForUnknownChangedDependency()
    {
        MainViewRefreshDecision decision = MainViewRefreshDecisionService.Build(
            MainViewUpdateMode.FolderFilterSelected,
            folderFilterApplied: false,
            keywordFilter: string.Empty,
            modeFilter: ChartModeFilter.All,
            sortColumnName: nameof(LibraryChartRow.Title),
            isPlaylistDetailView: false,
            dependency: MainViewDataDependency.Unknown,
            reason: "unknown_changed");

        Assert.AreEqual(MainViewRefreshAction.Refresh, decision.Action);
        Assert.AreEqual(MainViewDataDependency.Unknown, decision.Dependency);
        Assert.AreEqual(MainViewDataDependency.IdentitySortKey, decision.SortDependency);
    }

    /// <summary>source所属と同一性の変更は現在sortの種類にかかわらず一覧を再計算し、旧対象や旧行順を残しません。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void MainViewRefreshDecision_RefreshesWhenMembershipOrIdentityCanAffectCurrentView()
    {
        foreach (MainViewDataDependency dependency in new[] { MainViewDataDependency.SourceMembership, MainViewDataDependency.IdentitySortKey })
        {
            MainViewRefreshDecision decision = MainViewRefreshDecisionService.Build(
                MainViewUpdateMode.FolderFilterSelected,
                folderFilterApplied: false,
                keywordFilter: string.Empty,
                modeFilter: ChartModeFilter.All,
                sortColumnName: nameof(LibraryChartRow.rateDouble),
                isPlaylistDetailView: false,
                dependency: dependency,
                reason: "library_identity_changed");

            Assert.AreEqual(MainViewRefreshAction.Refresh, decision.Action, dependency.ToString());
            Assert.AreEqual(dependency, decision.Dependency, dependency.ToString());
        }
    }

    /// <summary>題名とパスの昇順・降順を互いに異なる順序の入力で確認し、列と方向の取り違えを検出します。</summary>
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
            [alphaLatePath, betaEarlyPath, gammaMiddlePath],
            ["z_alpha.bms", "a_beta.bms", "m_gamma.bms"]);
        AssertLibraryChartSort(
            nameof(LibraryChartRow.Title),
            ListSortDirection.Descending,
            [alphaLatePath, betaEarlyPath, gammaMiddlePath],
            ["m_gamma.bms", "a_beta.bms", "z_alpha.bms"]);
        AssertLibraryChartSort(
            nameof(LibraryChartRow.path),
            ListSortDirection.Ascending,
            [alphaLatePath, betaEarlyPath, gammaMiddlePath],
            ["a_beta.bms", "m_gamma.bms", "z_alpha.bms"]);
        AssertLibraryChartSort(
            nameof(LibraryChartRow.path),
            ListSortDirection.Descending,
            [alphaLatePath, betaEarlyPath, gammaMiddlePath],
            ["z_alpha.bms", "m_gamma.bms", "a_beta.bms"]);
    }

    /// <summary>notes・TOTAL・BPM・スコア率の各列指定で低値の行を高値の行より先へ並べ、指定した昇順の逆転を検出します。</summary>
    /// <remarks>各数値とpathが同じ順なので列の取り違えは検査しません。表示列の並替え先はCustomTableColumnFactoryTestsが分担します。</remarks>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void LibraryChartRowSortEngine_ChartInfoAndScoreColumnsUseTypedSort()
    {
        LibraryChartRow high = CreateLibraryChartRow("z_high.bms", "High", level: 1, chartNotes: 100, chartTotal: 10.0, chartMainBpm: 200.0, rateScorePerfect: 90);
        LibraryChartRow low = CreateLibraryChartRow("a_low.bms", "Low", level: 1, chartNotes: 20, chartTotal: 2.0, chartMainBpm: 100.0, rateScorePerfect: 20);

        AssertTypedSort(nameof(LibraryChartRow.ChartNotes), [high, low], ["a_low.bms", "z_high.bms"]);
        AssertTypedSort(nameof(LibraryChartRow.ChartTotalSortKey), [high, low], ["a_low.bms", "z_high.bms"]);
        AssertTypedSort(nameof(LibraryChartRow.ChartMainBpmSortKey), [high, low], ["a_low.bms", "z_high.bms"]);
        AssertTypedSort(nameof(LibraryChartRow.rateDouble), [high, low], ["a_low.bms", "z_high.bms"]);
    }

    /// <summary>実perfect/notesから率を再計算して保存済み旧rateを無視し、notesゼロは除算せず欠損値として表示します。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void RateDouble_ComputesFromScoreAndIgnoresStoredRate()
    {
        LibraryChartRow row = CreateLibraryChartRow("rate.bms", "Rate", level: 1, rateScorePerfect: 91);
        var score = new BMSScore { perfect = 91, totalnotes = 100, rate = 12 };
        row.UpdateSourceProjection(ChartFileProjection.WithScore(row.Chart,
            ChartScoreSnapshot.FromBmsScore(score, row.path)));

        Assert.AreEqual(0.91, row.rateDouble.GetValueOrDefault(), 0.000001);

        ChartFile zeroNotes = ChartTestValues.Empty();
        zeroNotes = zeroNotes with { Path = "zero.bms", Title = "Zero", Level = 1, Md5 = "55555555555555555555555555555555", RawTitle = "Zero", LevelText = (1).ToString() };
        zeroNotes = zeroNotes with { Score = ChartScoreSnapshot.FromBmsScore(new BMSScore { hash = zeroNotes.Md5, perfect = 10, totalnotes = 0 }, zeroNotes.Path) };

        Assert.IsFalse(LibraryChartRow.FromChartFile(zeroNotes).rateDouble.HasValue);
    }

    /// <summary>表summaryの譜面件数を数値として昇順・降順へ並べ、名前順や方向の取り違えを検出します。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void PlaylistSummarySortEngine_SortsAscendingAndDescending()
    {
        List<PlaylistSummaryRow> rows =
        [
            new PlaylistSummaryRow { PlaylistId = 1, Name = "B", TotalCharts = 30 },
            new PlaylistSummaryRow { PlaylistId = 2, Name = "A", TotalCharts = 10 },
            new PlaylistSummaryRow { PlaylistId = 3, Name = "C", TotalCharts = 20 }
        ];

        var asc = new ChartListSortParameters
        {
            ColumnsName = nameof(PlaylistSummaryRow.TotalCharts),
            Direction = ListSortDirection.Ascending
        };
        var desc = new ChartListSortParameters
        {
            ColumnsName = nameof(PlaylistSummaryRow.TotalCharts),
            Direction = ListSortDirection.Descending
        };

        List<PlaylistSummaryRow> ascSorted = PlaylistSummarySortEngine.Sort(rows, asc, out _);
        List<PlaylistSummaryRow> descSorted = PlaylistSummarySortEngine.Sort(rows, desc, out _);

        CollectionAssert.AreEqual(new[] { 2, 3, 1 }, ascSorted.Select(row => row.PlaylistId ?? -1).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 3, 2 }, descSorted.Select(row => row.PlaylistId ?? -1).ToArray());

    }

    /// <summary>主キー降順でもName/IDの同値順は昇順を保ち、null既定・未知列fallbackと全体整列/局所比較の一致を検証します。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void PlaylistSummarySortEngine_PreservesTieBreakDefaultsAndFallback()
    {
        PlaylistSummaryRow?[] rows = [new() { PlaylistId = 3, Name = "B", TotalCharts = 2 },
            new() { PlaylistId = 2, Name = "A", TotalCharts = 2 }, new() { PlaylistId = 1, Name = "a", TotalCharts = 2 },
            new() { PlaylistId = 4, Name = "C", TotalCharts = 1 }, null];
        var sort = new ChartListSortParameters { ColumnsName = nameof(PlaylistSummaryRow.TotalCharts), Direction = ListSortDirection.Descending };
        List<PlaylistSummaryRow> sorted = PlaylistSummarySortEngine.Sort(rows, sort, out _);
        CollectionAssert.AreEqual(new int?[] { 1, 2, 3, 4, null }, sorted.Select(row => row?.PlaylistId).ToArray());
        IComparer<PlaylistSummaryRow> comparer = PlaylistSummarySortEngine.CreateComparer(sort, out _);
        for (int index = 1; index < sorted.Count; index++) { Assert.IsTrue(comparer.Compare(sorted[index - 1], sorted[index]) <= 0); }
        sort.ColumnsName = "unknown";
        CollectionAssert.AreEqual(new int?[] { 4, 3, 1, 2, null }, PlaylistSummarySortEngine.Sort(rows, sort, out _).Select(row => row?.PlaylistId).ToArray());
    }

    /// <summary>表summaryのfolder名を名前と異なる順へ並べ、表名で代替して比較する誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void PlaylistSummarySortEngine_SortsFolderName()
    {
        List<PlaylistSummaryRow> rows =
        [
            new PlaylistSummaryRow { PlaylistId = 1, Name = "A", FolderName = "folder-b" },
            new PlaylistSummaryRow { PlaylistId = 2, Name = "B", FolderName = "folder-a" },
            new PlaylistSummaryRow { PlaylistId = 3, Name = "C", FolderName = "folder-c" }
        ];

        var sort = new ChartListSortParameters
        {
            ColumnsName = nameof(PlaylistSummaryRow.FolderName),
            Direction = ListSortDirection.Ascending
        };

        List<PlaylistSummaryRow> sorted = PlaylistSummarySortEngine.Sort(rows, sort, out _);

        CollectionAssert.AreEqual(new[] { 2, 1, 3 }, sorted.Select(row => row.PlaylistId ?? -1).ToArray());

    }

    /// <summary>playlist明細のclearとrank表示列を対応する比較値で並べ、同じscoreからsource/viewの率を一貫して算出します。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void PlaylistDetailSortEngine_ClearAndRankDisplayColumnsSort()
    {
        PlaylistDetailSourceRow hardAaa = CreatePlaylistDetailSourceRow("z_hard_aaa.bms", "Hard AAA", ClearType.HARD, RankType.AAA);
        PlaylistDetailSourceRow easyAa = CreatePlaylistDetailSourceRow("a_easy_aa.bms", "Easy AA", ClearType.EASY, RankType.AA);

        var clearSort = new ChartListSortParameters
        {
            ColumnsName = nameof(PlaylistDetailRow.clear),
            Direction = ListSortDirection.Ascending
        };
        var rankSort = new ChartListSortParameters
        {
            ColumnsName = nameof(PlaylistDetailRow.rank),
            Direction = ListSortDirection.Ascending
        };

        List<PlaylistDetailSourceRow> clearSorted = PlaylistDetailSortEngine.Sort([hardAaa, easyAa], clearSort, out _);
        List<PlaylistDetailSourceRow> rankSorted = PlaylistDetailSortEngine.Sort([hardAaa, easyAa], rankSort, out _);

        CollectionAssert.AreEqual(new[] { "a_easy_aa.bms", "z_hard_aaa.bms" }, clearSorted.Select(row => row.path).ToArray());
        CollectionAssert.AreEqual(new[] { "a_easy_aa.bms", "z_hard_aaa.bms" }, rankSorted.Select(row => row.path).ToArray());

        Assert.AreEqual(0.95, hardAaa.rateDouble.GetValueOrDefault(), 0.000001);
        Assert.AreEqual(0.95, hardAaa.CreateViewRow().rateDouble.GetValueOrDefault(), 0.000001);
    }

    /// <summary>beatoraja DBから直接読むlamp整数の互換値と表示名を維持し、未知lampも数値で表示して欠落させません。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void ClearType_PreservesBeatorajaNumericValuesAndDisplayText()
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

        var converter = new cleartypeToStringConvberter();
        Assert.AreEqual("PERFECT", converter.Convert(ClearType.PA, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.AreEqual("99", converter.Convert((ClearType)99, typeof(string), null, CultureInfo.InvariantCulture));
    }

    /// <summary>失敗・ASSIST・EASY・MAXをlamp達成順へ並べ、表示名の文字列順やLR2保存整数順を使いません。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void LibraryChartRowClearSortUsesNumericClearType()
    {
        LibraryChartRow max = CreateLibraryChartRow("z_max.bms", "Max", 1, clear: ClearType.MAX);
        LibraryChartRow assist = CreateLibraryChartRow("m_assist.bms", "Assist", 1, clear: ClearType.INVALID);
        LibraryChartRow easy = CreateLibraryChartRow("a_easy.bms", "Easy", 1, clear: ClearType.EASY);
        LibraryChartRow failed = CreateLibraryChartRow("b_failed.bms", "Failed", 1, clear: ClearType.FAILED);

        var clearSort = new ChartListSortParameters
        {
            ColumnsName = nameof(LibraryChartRow.clear),
            Direction = ListSortDirection.Ascending
        };
        List<LibraryChartRow> librarySorted = LibraryChartRowSortEngine.SortForMainView([max, assist, easy, failed], ToSortSpecification(clearSort), isPlaylistDetailView: false, out _);

        CollectionAssert.AreEqual(new[] { "b_failed.bms", "m_assist.bms", "a_easy.bms", "z_max.bms" }, librarySorted.Select(row => row.path).ToArray());

    }

    /// <summary>LR2の保存整数を内部lampへ復元し、ASSIST・EX HARD・MAXを従来の保存値へ変換して既存DBとの互換性を保ちます。</summary>
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

    /// <summary>LR2の同じclear整数でもoption履歴のEASY/ASSISTとPERFECTを区別し、未指定や複合optionで到達lampを誤分類しません。</summary>
    [TestMethod]
    [TestCategory("SortEngine")]
    public void Lr2ScoreClearResolutionUsesOptionHistoryForAssistAndPerfect()
    {
        Assert.AreEqual(
            ClearType.INVALID,
            ClearTypeStorageConverter.FromLr2ScoreValue(2, ClearTypeStorageConverter.OptionHistoryAssist));
        Assert.AreEqual(
            ClearType.EASY,
            ClearTypeStorageConverter.FromLr2ScoreValue(
                2,
                ClearTypeStorageConverter.OptionHistoryAssist | ClearTypeStorageConverter.OptionHistoryEasy));
        Assert.AreEqual(ClearType.EASY, ClearTypeStorageConverter.FromLr2ScoreValue(2, ClearTypeStorageConverter.OptionHistoryEasy));
        Assert.AreEqual(ClearType.INVALID, ClearTypeStorageConverter.FromLr2ScoreValue(2, 0));
        Assert.AreEqual(
            ClearType.PA,
            ClearTypeStorageConverter.FromLr2ScoreValue(5, ClearTypeStorageConverter.OptionHistoryPerfect));
    }

    private static void AssertTypedSort(string columnName, IReadOnlyList<LibraryChartRow> source, string[] expectedPaths)
    {
        var sortParameters = new ChartListSortParameters
        {
            ColumnsName = columnName,
            Direction = ListSortDirection.Ascending
        };

        List<LibraryChartRow> sorted = LibraryChartRowSortEngine.SortForMainView(source, ToSortSpecification(sortParameters), isPlaylistDetailView: false, out _);

        CollectionAssert.AreEqual(expectedPaths, sorted.Select(row => row.path).ToArray(), columnName + " must use typed sort.");

    }

    private static void AssertLibraryChartSort(string columnName, ListSortDirection direction, IReadOnlyList<LibraryChartRow> source, string[] expectedPaths)
    {
        var sortParameters = new ChartListSortParameters
        {
            ColumnsName = columnName,
            Direction = direction
        };

        List<LibraryChartRow> sorted = LibraryChartRowSortEngine.SortForMainView(source, ToSortSpecification(sortParameters), isPlaylistDetailView: false, out _);

        CollectionAssert.AreEqual(expectedPaths, sorted.Select(row => row.path).ToArray(), columnName + " " + direction + " order mismatch.");

    }

    private static LibraryChartRow CreateLibraryChartRow(string path, string title, int level, string folder = "", int? chartNotes = null, double? chartTotal = null, double? chartMainBpm = null, int? rateScorePerfect = null, ClearType? clear = null)
    {
        string hash = CreateMd5FromPath(path);
        ChartFile file = ChartTestValues.Empty();
        file = file with { Path = path, Title = title, Level = level, Md5 = hash, RawTitle = title, LevelText = (level).ToString() };
        file = file with { Folder = folder };
        BeMusicSeeker.Models.ChartDetails chartInfo = null!;
        if (chartNotes.HasValue || chartTotal.HasValue || chartMainBpm.HasValue)
        {
            chartInfo = new BeMusicSeeker.Models.ChartDetails
            {
                sha256 = CreateSha256FromPath(path),
                md5 = hash,
                charthash = CreateSha256FromPath(path + ":chart"),
                notes = chartNotes ?? 0,
                total = chartTotal,
                mainbpm = chartMainBpm,
                parser_version = 1
            };
        }
        if (rateScorePerfect.HasValue)
        {
            file = file with
            {
                Score = ChartScoreSnapshot.FromBmsScore(new BMSScore
                {
                    hash = hash,
                    perfect = rateScorePerfect.Value,
                    totalnotes = 100
                }, file.Path)
            };
        }
        if (clear.HasValue)
        {
            file = file with
            {
                Score = ChartScoreSnapshot.FromBmsScore(new BMSScore
                {
                    hash = hash,
                    clear = clear.Value,
                    rank = RankType.A,
                    perfect = 80,
                    totalnotes = 100
                }, file.Path)
            };
        }
        var row = LibraryChartRow.FromChartFile(file);
        if (chartInfo != null)
        {
            row.SetChartInfoProjectionProvider(CreateChartInfoProvider(chartInfo));
        }
        return row;
    }

    private static Func<ChartFile, BeMusicSeeker.Models.ChartDetails> CreateChartInfoProvider(params BeMusicSeeker.Models.ChartDetails[] rows)
    {
        return chart =>
        {
            if (chart == null)
            {
                return null!;
            }
            return (rows ?? [])
                .Where(row => row != null)
                .FirstOrDefault(row => !string.IsNullOrWhiteSpace(chart.Sha256) && string.Equals(row.sha256, chart.Sha256, StringComparison.OrdinalIgnoreCase))
                ?? (rows ?? [])
                    .Where(row => row != null)
                    .FirstOrDefault(row => !string.IsNullOrWhiteSpace(chart.Md5) && string.Equals(row.md5, chart.Md5, StringComparison.OrdinalIgnoreCase))
                ?? null!;
        };
    }

    private static PlaylistDetailSourceRow CreatePlaylistDetailSourceRow(string path, string title, ClearType clear, RankType rank)
    {
        string hash = CreateMd5FromPath(path);
        ChartFile file = ChartTestValues.Empty();
        file = file with { Path = path, Title = title, Level = 1, Md5 = hash, RawTitle = title, LevelText = (1).ToString() };
        file = file with
        {
            Score = ChartScoreSnapshot.FromBmsScore(new BMSScore
            {
                hash = hash,
                clear = clear,
                rank = rank,
                perfect = rank == RankType.AAA ? 95 : 85,
                totalnotes = 100
            }, file.Path)
        };
        return new PlaylistDetailSourceRow(new BMSTableEntry(file), (file));
    }

    private static string CreateMd5FromPath(string path)
    {
        using var md5 = MD5.Create();
        byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(path));
        return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static string CreateSha256FromPath(string path)
    {
        using var sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(path));
        return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static ChartListSortSpecification ToSortSpecification(ChartListSortParameters sort)
    {
        return ChartListSortSpecification.Create(
            sort?.ColumnsName,
            sort?.Direction ?? ListSortDirection.Ascending,
            sort != null);
    }

}
