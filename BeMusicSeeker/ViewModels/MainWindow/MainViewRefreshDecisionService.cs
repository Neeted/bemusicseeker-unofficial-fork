using System;
using System.Collections.Generic;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Resolves pure main-view refresh decisions outside the shell ViewModel so list ownership can move incrementally.
/// </summary>
internal static class MainViewRefreshDecisionService
{
    /// <summary>
    /// Reason used when a BMS path mutation invalidates normal-library sort keys.
    /// </summary>
    internal const string NormalLibraryBmsPathChangedReason = "bms_path_changed";

    /// <summary>
    /// Reason used when a BMS title mutation invalidates normal-library sort keys.
    /// </summary>
    internal const string NormalLibraryBmsTitleChangedReason = "bms_title_changed";

    /// <summary>
    /// Reason used when a bmson path mutation invalidates normal-library sort keys.
    /// </summary>
    internal const string NormalLibraryBmsonPathChangedReason = "bmson_path_changed";

    /// <summary>
    /// Reason used when bmson source identity changes and source rows must be rebuilt.
    /// </summary>
    internal const string NormalLibraryBmsonSourceIdentityChangedReason = "bmson_source_identity_changed";

    /// <summary>
    /// Reason used when bmson sort keys change without changing source identity.
    /// </summary>
    internal const string NormalLibraryBmsonSortKeyChangedReason = "bmson_sort_key_changed";

    /// <summary>
    /// Reason used when chart info digest backfill invalidates normal-library sort keys.
    /// </summary>
    internal const string NormalLibraryChartInfoDigestBackfilledReason = "chart_info_digest_backfilled";

    /// <summary>
    /// Reason used when install destination projection invalidates normal-library sort keys.
    /// </summary>
    internal const string NormalLibraryInstallDestinationChangedReason = "install_destination_changed";

    /// <summary>
    /// Reason used when reference table symbols invalidate normal-library sort keys.
    /// </summary>
    internal const string NormalLibraryReferenceTablesChangedReason = "ref_tables_changed";

    /// <summary>
    /// Reason used when maintenance projection invalidates normal-library sort keys.
    /// </summary>
    internal const string NormalLibraryMaintenanceChangedReason = "maintenance_changed";

    /// <summary>
    /// Reason used when warning projection invalidates normal-library sort keys.
    /// </summary>
    internal const string NormalLibraryWarningChangedReason = "warning_changed";

    /// <summary>
    /// 現在の表示の依存関係から、表示値だけの更新か並べ替え・絞込みの再構築かを判断します。
    /// 導入先だけの変更では、所属が変わらず検索・並べ替えにも影響しない一覧を保持します。
    /// </summary>
    /// <param name="currentMode">Current tree or request mode shown by the main view.</param>
    /// <param name="folderFilterApplied">Whether a folder-level virtual normal-library filter is applied.</param>
    /// <param name="keywordFilter">Current keyword filter text.</param>
    /// <param name="modeFilter">Current BMS mode filter.</param>
    /// <param name="sortColumnName">Current sort column name.</param>
    /// <param name="isPlaylistDetailView">Whether the current rows are playlist detail rows.</param>
    /// <param name="dependency">Changed data dependency.</param>
    /// <param name="reason">Diagnostic reason text to carry into the decision.</param>
    /// <returns>Main-view refresh decision preserving the existing refresh/display-refresh split.</returns>
    internal static MainViewRefreshDecision Build(
        MainViewUpdateMode currentMode,
        bool folderFilterApplied,
        string keywordFilter,
        ChartModeFilter modeFilter,
        string sortColumnName,
        bool isPlaylistDetailView,
        MainViewDataDependency dependency,
        string reason)
    {
        MainViewDataDependency sortDependency = GetSortColumnDependency(sortColumnName);
        // 導入先と推定警告の変更は、既存ファイル・保留項目の所属やモードを変えない。
        // キーワード検索と依存する並べ替えだけが表示集合の再評価を必要とする。
        if (dependency == MainViewDataDependency.InstallDestination
            && !isPlaylistDetailView
            && string.IsNullOrWhiteSpace(keywordFilter)
            && currentMode is MainViewUpdateMode.FolderFilterSelected
                or MainViewUpdateMode.FullScanAllChartsFilterSelected
                or MainViewUpdateMode.FileMissingFilterSelected
                or MainViewUpdateMode.FileMissingIgnoredFilterSelected
                or MainViewUpdateMode.NewlyInstalledFolderSelected
                or MainViewUpdateMode.PendingInstallFolderSelected
            && IsDisplayRefreshEnough(sortDependency, dependency))
        {
            return new MainViewRefreshDecision(MainViewRefreshAction.RefreshDisplay, dependency, sortDependency, reason, "install_destination_update_does_not_affect_current_sort_or_filter");
        }
        bool fullNormalLibraryView = currentMode == MainViewUpdateMode.FolderFilterSelected
            && !folderFilterApplied
            && string.IsNullOrWhiteSpace(keywordFilter)
            && modeFilter == ChartModeFilter.All
            && !isPlaylistDetailView;
        if (!fullNormalLibraryView)
        {
            if (IsDuplicateSubsetDisplayRefreshEnough(currentMode, keywordFilter, modeFilter, isPlaylistDetailView, sortDependency, dependency))
            {
                return new MainViewRefreshDecision(MainViewRefreshAction.RefreshDisplay, dependency, sortDependency, reason, "duplicate_subset_dependency_update_does_not_affect_current_sort_or_filter");
            }

            return new MainViewRefreshDecision(MainViewRefreshAction.Refresh, dependency, sortDependency, reason, "not_full_normal_library");
        }

        if (IsDisplayRefreshEnough(sortDependency, dependency))
        {
            return new MainViewRefreshDecision(MainViewRefreshAction.RefreshDisplay, dependency, sortDependency, reason, "dependency_update_does_not_affect_current_sort_or_filter");
        }

        return new MainViewRefreshDecision(MainViewRefreshAction.Refresh, dependency, sortDependency, reason, "dependency_affects_current_view");
    }

    /// <summary>
    /// Classifies which main-view data dependency can affect the specified sort column.
    /// </summary>
    /// <param name="columnName">Column name from the current sort parameters.</param>
    /// <returns>Dependency family for the sort column, or <see cref="MainViewDataDependency.Unknown"/>.</returns>
    internal static MainViewDataDependency GetSortColumnDependency(string columnName)
    {
        if (ChartListOrder.TryGetVirtualSortColumnMetadata(columnName, out ChartListOrderColumnMetadata metadata))
        {
            return metadata.Dependency;
        }

        if (string.Equals(columnName, nameof(LibraryChartRow.instl_dst), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.InstallDestinationTitle), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.InstallDestinationArtist), StringComparison.Ordinal))
        {
            return MainViewDataDependency.InstallDestination;
        }

        if (string.Equals(columnName, nameof(LibraryChartRow.RefTablesSymbols), StringComparison.Ordinal))
        {
            return MainViewDataDependency.ReferenceTables;
        }

        if (IsScoreSortColumn(columnName))
        {
            return MainViewDataDependency.Score;
        }

        if (IsChartInfoSortColumn(columnName))
        {
            return MainViewDataDependency.ChartInfo;
        }

        if (IsMaintenanceSortColumn(columnName))
        {
            return MainViewDataDependency.Maintenance;
        }

        if (IsWarningSortColumn(columnName))
        {
            return MainViewDataDependency.Warning;
        }

        return MainViewDataDependency.Unknown;
    }

    /// <summary>
    /// Gets sort-key invalidation reasons for path mutations split by chart family.
    /// </summary>
    /// <param name="hasBmsPathMutation">Whether BMS paths changed.</param>
    /// <param name="hasBmsonPathMutation">Whether bmson paths changed.</param>
    /// <returns>Ordered invalidation reasons consumed by the normal-library refresh pipeline.</returns>
    internal static IReadOnlyList<string> GetPathSortKeyInvalidationReasons(bool hasBmsPathMutation, bool hasBmsonPathMutation)
    {
        var reasons = new List<string>(2);
        if (hasBmsPathMutation)
        {
            reasons.Add(NormalLibraryBmsPathChangedReason);
        }

        if (hasBmsonPathMutation)
        {
            reasons.Add(NormalLibraryBmsonPathChangedReason);
        }

        return reasons;
    }

    /// <summary>
    /// Gets the complete ordered list of normal-library sort-key invalidation reasons.
    /// </summary>
    /// <returns>Reasons used by tests to keep refresh invalidation coverage explicit.</returns>
    internal static IReadOnlyList<string> GetSortKeyInvalidationReasons()
    {
        return
        [
            NormalLibraryBmsTitleChangedReason,
            NormalLibraryBmsPathChangedReason,
            NormalLibraryBmsonPathChangedReason,
            NormalLibraryBmsonSourceIdentityChangedReason,
            NormalLibraryBmsonSortKeyChangedReason,
            NormalLibraryChartInfoDigestBackfilledReason,
            NormalLibraryInstallDestinationChangedReason,
            NormalLibraryReferenceTablesChangedReason,
            NormalLibraryMaintenanceChangedReason,
            NormalLibraryWarningChangedReason
        ];
    }

    /// <summary>
    /// Determines whether source rows must be cleared for a normal-library sort-key invalidation reason.
    /// </summary>
    /// <param name="reason">Invalidation reason, potentially with a suffix added by a caller.</param>
    /// <returns><see langword="true"/> when source identity changed and cached source rows are unsafe.</returns>
    internal static bool ShouldClearSourceRowsForSortKeyChange(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        return reason.IndexOf(NormalLibraryBmsPathChangedReason, StringComparison.Ordinal) >= 0
            || reason.IndexOf(NormalLibraryBmsTitleChangedReason, StringComparison.Ordinal) >= 0
            || reason.IndexOf(NormalLibraryBmsonPathChangedReason, StringComparison.Ordinal) >= 0
            || reason.IndexOf(NormalLibraryBmsonSourceIdentityChangedReason, StringComparison.Ordinal) >= 0;
    }

    /// <summary>
    /// Determines whether virtual normal-library rows can satisfy the specified view update mode.
    /// </summary>
    /// <param name="mode">View update mode to classify.</param>
    /// <returns><see langword="true"/> when the virtual normal-library route supports the mode.</returns>
    internal static bool IsVirtualNormalLibraryModeSupported(MainViewUpdateMode mode)
    {
        return mode == MainViewUpdateMode.TreeViewFilterNotChanged
            || mode == MainViewUpdateMode.FolderFilterSelected
            || mode == MainViewUpdateMode.FullScanAllChartsFilterSelected
            || mode == MainViewUpdateMode.KeywordFilterUpdated
            || mode == MainViewUpdateMode.ModeFilterUpdated
            || mode == MainViewUpdateMode.SortUpdated;
    }

    /// <summary>
    /// Determines whether an incremental regular-folder stage must fall back to full rebuild.
    /// </summary>
    /// <param name="mode">Requested update mode.</param>
    /// <param name="hasFolderView">Whether the folder-stage view is already available.</param>
    /// <param name="hasKeywordView">Whether the keyword-stage view is already available.</param>
    /// <param name="hasModeView">Whether the mode-stage view is already available.</param>
    /// <param name="currentTreeMode">Current tree selection mode.</param>
    /// <returns><see langword="true"/> when a missing cached stage requires rebuilding from the folder stage.</returns>
    internal static bool ShouldRebuildRegularFolderStage(
        MainViewUpdateMode mode,
        bool hasFolderView,
        bool hasKeywordView,
        bool hasModeView,
        MainViewUpdateMode currentTreeMode)
    {
        if (IsPlaylistTreeActive(mode, currentTreeMode))
        {
            return false;
        }

        if (mode < MainViewUpdateMode.KeywordFilterUpdated)
        {
            return false;
        }

        return !hasFolderView || !hasKeywordView || !hasModeView;
    }

    private static bool IsDuplicateSubsetDisplayRefreshEnough(
        MainViewUpdateMode currentMode,
        string keywordFilter,
        ChartModeFilter modeFilter,
        bool isPlaylistDetailView,
        MainViewDataDependency sortDependency,
        MainViewDataDependency changedDependency)
    {
        if (currentMode != MainViewUpdateMode.DuplicateFilterSelected
            || isPlaylistDetailView
            || !string.IsNullOrWhiteSpace(keywordFilter)
            || modeFilter != ChartModeFilter.All)
        {
            return false;
        }

        return IsDisplayRefreshEnough(sortDependency, changedDependency);
    }

    private static bool IsDisplayRefreshEnough(MainViewDataDependency sortDependency, MainViewDataDependency changedDependency)
    {
        if (sortDependency == MainViewDataDependency.Unknown || sortDependency == changedDependency)
        {
            return false;
        }

        if (changedDependency == MainViewDataDependency.InstallDestination
            && sortDependency == MainViewDataDependency.Warning)
        {
            return false;
        }

        switch (changedDependency)
        {
            case MainViewDataDependency.ChartInfo:
            case MainViewDataDependency.Score:
            case MainViewDataDependency.Maintenance:
            case MainViewDataDependency.Warning:
            case MainViewDataDependency.InstallDestination:
            case MainViewDataDependency.ReferenceTables:
                break;
            default:
                return false;
        }

        return sortDependency == MainViewDataDependency.IdentitySortKey
            || sortDependency == MainViewDataDependency.InstallDestination
            || sortDependency == MainViewDataDependency.ChartInfo
            || sortDependency == MainViewDataDependency.Score
            || sortDependency == MainViewDataDependency.Maintenance
            || sortDependency == MainViewDataDependency.Warning
            || sortDependency == MainViewDataDependency.ReferenceTables;
    }

    private static bool IsScoreSortColumn(string columnName)
    {
        return string.Equals(columnName, nameof(LibraryChartRow.clear), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.rank), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.rate), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.rateDouble), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.score), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.totalnotes), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.maxcombo), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.minbp), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ranking), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.rankingNum), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.rankingString), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.rankingLastupdate), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.stddevVal), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.scoreDifficulty), StringComparison.Ordinal);
    }

    private static bool IsChartInfoSortColumn(string columnName)
    {
        return string.Equals(columnName, nameof(LibraryChartRow.ChartLevelSortKey), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartDifficultySortKey), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartMainBpmSortKey), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartMaxBpmSortKey), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartMinBpmSortKey), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartDurationSortKey), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartJudgeSortKey), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartFeatureSortKey), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartNotes), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartLongNotes), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartScratchNotes), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartTotalSortKey), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartTotalPerNoteSortKey), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartDensitySortKey), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartPeakDensitySortKey), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartEndDensitySortKey), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartSoflanCount), StringComparison.Ordinal);
    }

    private static bool IsMaintenanceSortColumn(string columnName)
    {
        return string.Equals(columnName, nameof(LibraryChartRow.WAVHealth), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.BGAHealth), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.MovieHealth), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.encoding), StringComparison.Ordinal);
    }

    private static bool IsWarningSortColumn(string columnName)
    {
        return string.Equals(columnName, nameof(LibraryChartRow.WarningDigestText), StringComparison.Ordinal);
    }

    private static bool IsPlaylistTreeActive(MainViewUpdateMode mode, MainViewUpdateMode currentTreeMode)
    {
        return ChartListRefreshCoordinator.IsPlaylistTreeActive(mode, currentTreeMode);
    }
}
