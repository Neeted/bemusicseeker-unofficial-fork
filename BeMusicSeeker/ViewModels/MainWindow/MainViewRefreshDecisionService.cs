using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 現在の表示条件と変更依存から、行保持と正規の並べ替え・所属更新を判断します。
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
    /// <param name="currentMode">現在のツリー選択・表示要求のモード。</param>
    /// <param name="folderFilterApplied">通常一覧にフォルダ単位の絞込みがあるか。</param>
    /// <param name="keywordFilter">現在の検索文字列。</param>
    /// <param name="modeFilter">現在の譜面モード条件。</param>
    /// <param name="sortColumnName">現在の並べ替え列名。</param>
    /// <param name="isPlaylistDetailView">プレイリスト詳細を表示しているか。</param>
    /// <param name="dependency">変化した値の依存。</param>
    /// <param name="reason">判断結果へ渡す診断用の理由。</param>
    /// <returns>表示更新と正規の一覧更新を区別する既存の判断結果。</returns>
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
        if (currentMode == MainViewUpdateMode.NewlyInstalledFolderSelected
            && !isPlaylistDetailView && string.IsNullOrWhiteSpace(keywordFilter)
            && modeFilter == ChartModeFilter.All && IsDisplayRefreshEnough(sortDependency, dependency))
        {
            return new MainViewRefreshDecision(MainViewRefreshAction.RefreshDisplay, dependency, sortDependency, reason, "installed_dependency_does_not_affect_sort_or_filter");
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

    /// <summary>一通知batchの全依存を合成し、導入済み項目の確定差分から必要な最終更新を一度判断します。</summary>
    /// <param name="batch">未消費の全通知を合成した短命の変更事実。</param>
    /// <param name="mode">現在の表示モード。</param>
    /// <param name="selectedPackage">選択中の導入済み所属。全所属表示ではnull。</param>
    /// <param name="folderFilterApplied">通常一覧のフォルダ絞込みの有無。</param>
    /// <param name="keyword">現在の検索文字列。</param>
    /// <param name="modeFilter">現在の譜面モード条件。</param>
    /// <param name="sortColumn">現在の並べ替え列。</param>
    /// <param name="playlistDetail">プレイリスト詳細を表示しているか。</param>
    /// <returns>全変更依存に必要な一回の表示更新または一覧更新。</returns>
    internal static MainViewRefreshAction BuildNotificationBatch(
        NormalLibraryRefreshNotificationBatch batch, MainViewUpdateMode mode, ChartPackage selectedPackage,
        bool folderFilterApplied, string keyword, ChartModeFilter modeFilter, string sortColumn, bool playlistDetail)
    {
        if (batch.ResetsPriorNotifications || batch.DeletedTokens.Count > 0)
        {
            return MainViewRefreshAction.Refresh;
        }
        bool installed = mode == MainViewUpdateMode.NewlyInstalledFolderSelected && !playlistDetail;
        if (batch.HasEffect(LibraryChartRefreshEffects.SourceChanged))
        {
            // 全source通知で、対象entryの旧新値を取得できなかった追加・置換は通常更新へ戻す。
            var coveredTokens = new HashSet<OwnedChartToken>(batch.InstalledChartChanges.Select(change => change.Current.Token));
            if (!installed || batch.ChangedCharts.Count == 0
                || batch.ChangedCharts.Any(chart => chart.Token == null || !coveredTokens.Contains(chart.Token)))
            {
                return MainViewRefreshAction.Refresh;
            }
            foreach (InstalledChartCurrentChange change in batch.InstalledChartChanges)
            {
                if (selectedPackage != null && !change.Packages.Contains(selectedPackage))
                {
                    continue;
                }
                if (BasicChangeAffectsView(change.Before, change.Current, keyword, modeFilter, sortColumn))
                {
                    return MainViewRefreshAction.Refresh;
                }
            }
        }
        var dependencies = new List<MainViewDataDependency>();
        if (batch.HasEffect(LibraryChartRefreshEffects.InstallDestinationOverlayChanged))
        {
            dependencies.Add(MainViewDataDependency.InstallDestination);
        }
        if (batch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged))
        {
            dependencies.Add(MainViewDataDependency.Warning);
        }
        if (batch.HasEffect(LibraryChartRefreshEffects.MaintenancePresentationChanged))
        {
            dependencies.Add(MainViewDataDependency.Maintenance);
        }
        if (batch.ChangedDetailMd5s.Count > 0 || batch.ChangedDetailSha256s.Count > 0)
        {
            dependencies.Add(MainViewDataDependency.ChartInfo);
        }
        foreach (MainViewDataDependency dependency in dependencies)
        {
            if (Build(mode, folderFilterApplied, keyword, modeFilter, sortColumn, playlistDetail, dependency, "normal_library_batch").ShouldRefresh)
            {
                return MainViewRefreshAction.Refresh;
            }
        }
        return MainViewRefreshAction.RefreshDisplay;
    }

    private static bool BasicChangeAffectsView(ChartFile before, ChartFile current, string keyword, ChartModeFilter modeFilter, string column)
    {
        if (before == null || current == null || before.Token != current.Token)
        {
            return true;
        }
        bool titleChanged = before.Title != current.Title;
        bool pathChanged = before.Path != current.Path;
        bool modeChanged = before.Mode != current.Mode;
        bool otherBasicChanged = before.Artist != current.Artist || before.Genre != current.Genre
            || before.Level != current.Level || before.LevelText != current.LevelText || before.Difficulty != current.Difficulty
            || before.Md5 != current.Md5 || before.Sha256 != current.Sha256 || before.Tag != current.Tag
            || before.Favorite != current.Favorite || before.AddDate != current.AddDate || before.Date != current.Date
            || before.Txt != current.Txt || before.Judge != current.Judge || before.Subtitle != current.Subtitle
            || before.Folder != current.Folder;
        if (!string.IsNullOrWhiteSpace(keyword) && (titleChanged || pathChanged || modeChanged || otherBasicChanged))
        {
            return true;
        }
        if (modeFilter != ChartModeFilter.All && modeChanged)
        {
            return true;
        }
        // Titleは他列の同値時にも既存の第二ソートキーです。
        if (titleChanged)
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(column) || column == nameof(LibraryChartRow.Title))
        {
            return false;
        }
        if (column == nameof(LibraryChartRow.path) || column == nameof(LibraryChartRow.Folder))
        {
            return pathChanged || before.Folder != current.Folder;
        }
        if (column == nameof(LibraryChartRow.mode))
        {
            return modeChanged;
        }
        if (GetSortColumnDependency(column) == MainViewDataDependency.IdentitySortKey)
        {
            return otherBasicChanged;
        }
        if (GetSortColumnDependency(column) == MainViewDataDependency.Unknown)
        {
            return true;
        }
        // hash更新は詳細・scoreの解決先も変える。
        return before.Md5 != current.Md5 || before.Sha256 != current.Sha256;
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
