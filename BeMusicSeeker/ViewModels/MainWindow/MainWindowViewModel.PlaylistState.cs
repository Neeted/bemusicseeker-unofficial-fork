using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

public partial class MainWindowViewModel
{
    public enum PlaylistFilterType
    {
        PlaylistFilter = 1,
        PlaylistNotOwnedFilterSelected
    }

    /// <summary>
    /// playlist source snapshot の再構築要否を判定する正規化済み snapshot です。
    /// </summary>
    internal readonly struct PlaylistSourceIdentity : IEquatable<PlaylistSourceIdentity>
    {
        internal BMSTable Table { get; }

        internal string FolderName { get; }

        internal PlaylistFilterType FilterType { get; }

        internal long LibraryIndexVersion { get; }

        internal long PlaylistRevision { get; }

        internal int ScoreSnapshotVersion { get; }

        internal int ChartInfoIndexVersion { get; }

        internal bool HasResolvedSelection { get; }

        internal PlaylistSourceIdentity(BMSTable table, string folderName, PlaylistFilterType filterType, long libraryIndexVersion, long playlistRevision, int scoreSnapshotVersion, int chartInfoIndexVersion, bool hasResolvedSelection)
        {
            Table = table;
            FolderName = folderName;
            FilterType = filterType;
            LibraryIndexVersion = libraryIndexVersion;
            PlaylistRevision = playlistRevision;
            ScoreSnapshotVersion = scoreSnapshotVersion;
            ChartInfoIndexVersion = chartInfoIndexVersion;
            HasResolvedSelection = hasResolvedSelection;
        }

        public bool Equals(PlaylistSourceIdentity other)
        {
            return Table == other.Table && string.Equals(FolderName, other.FolderName, StringComparison.Ordinal) && FilterType == other.FilterType && LibraryIndexVersion == other.LibraryIndexVersion && PlaylistRevision == other.PlaylistRevision && ScoreSnapshotVersion == other.ScoreSnapshotVersion && ChartInfoIndexVersion == other.ChartInfoIndexVersion && HasResolvedSelection == other.HasResolvedSelection;
        }

        internal bool EqualsIgnoringChartInfoIndex(PlaylistSourceIdentity other)
        {
            return Table == other.Table && string.Equals(FolderName, other.FolderName, StringComparison.Ordinal) && FilterType == other.FilterType && LibraryIndexVersion == other.LibraryIndexVersion && PlaylistRevision == other.PlaylistRevision && ScoreSnapshotVersion == other.ScoreSnapshotVersion && HasResolvedSelection == other.HasResolvedSelection;
        }

        public override bool Equals(object obj)
        {
            return obj is PlaylistSourceIdentity other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = Table?.GetHashCode() ?? 0;
                hashCode = (hashCode * 397) ^ (FolderName?.GetHashCode() ?? 0);
                hashCode = (hashCode * 397) ^ (int)FilterType;
                hashCode = (hashCode * 397) ^ LibraryIndexVersion.GetHashCode();
                hashCode = (hashCode * 397) ^ PlaylistRevision.GetHashCode();
                hashCode = (hashCode * 397) ^ ScoreSnapshotVersion;
                hashCode = (hashCode * 397) ^ ChartInfoIndexVersion;
                hashCode = (hashCode * 397) ^ HasResolvedSelection.GetHashCode();
                return hashCode;
            }
        }
    }

    /// <summary>
    /// playlist source snapshot から view を再 materialize する条件を表す snapshot です。
    /// </summary>
    internal readonly struct PlaylistPresentationIdentity : IEquatable<PlaylistPresentationIdentity>
    {
        internal string KeywordFilter { get; }

        internal ModeFilterType ModeFilter { get; }

        internal string SortColumnName { get; }

        internal ListSortDirection SortDirection { get; }

        internal PlaylistPresentationIdentity(string keywordFilter, ModeFilterType modeFilter, string sortColumnName, ListSortDirection sortDirection)
        {
            KeywordFilter = keywordFilter;
            ModeFilter = modeFilter;
            SortColumnName = sortColumnName;
            SortDirection = sortDirection;
        }

        public bool Equals(PlaylistPresentationIdentity other)
        {
            return string.Equals(KeywordFilter, other.KeywordFilter, StringComparison.Ordinal) && ModeFilter == other.ModeFilter && string.Equals(SortColumnName, other.SortColumnName, StringComparison.Ordinal) && SortDirection == other.SortDirection;
        }

        public override bool Equals(object obj)
        {
            return obj is PlaylistPresentationIdentity other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = KeywordFilter?.GetHashCode() ?? 0;
                hashCode = (hashCode * 397) ^ (int)ModeFilter;
                hashCode = (hashCode * 397) ^ (SortColumnName?.GetHashCode() ?? 0);
                hashCode = (hashCode * 397) ^ (int)SortDirection;
                return hashCode;
            }
        }
    }

    /// <summary>
    /// playlist build 要求の同値判定に使う正規化済み snapshot です。
    /// </summary>
    internal readonly struct PlaylistRequestIdentity : IEquatable<PlaylistRequestIdentity>
    {
        internal PlaylistSourceIdentity SourceIdentity { get; }

        internal PlaylistPresentationIdentity PresentationIdentity { get; }

        internal BMSTable Table => SourceIdentity.Table;

        internal string FolderName => SourceIdentity.FolderName;

        internal PlaylistFilterType FilterType => SourceIdentity.FilterType;

        internal string KeywordFilter => PresentationIdentity.KeywordFilter;

        internal ModeFilterType ModeFilter => PresentationIdentity.ModeFilter;

        internal string SortColumnName => PresentationIdentity.SortColumnName;

        internal ListSortDirection SortDirection => PresentationIdentity.SortDirection;

        internal long LibraryIndexVersion => SourceIdentity.LibraryIndexVersion;

        internal long PlaylistRevision => SourceIdentity.PlaylistRevision;

        internal int ScoreSnapshotVersion => SourceIdentity.ScoreSnapshotVersion;

        internal int ChartInfoIndexVersion => SourceIdentity.ChartInfoIndexVersion;

        internal bool HasResolvedSelection => SourceIdentity.HasResolvedSelection;

        /// <summary>
        /// 正規化済み playlist 要求 identity を生成します。
        /// </summary>
        internal PlaylistRequestIdentity(BMSTable table, string folderName, PlaylistFilterType filterType, string keywordFilter, ModeFilterType modeFilter, string sortColumnName, ListSortDirection sortDirection, long libraryIndexVersion, long playlistRevision, int scoreSnapshotVersion, int chartInfoIndexVersion, bool hasResolvedSelection)
        {
            SourceIdentity = new PlaylistSourceIdentity(table, folderName, filterType, libraryIndexVersion, playlistRevision, scoreSnapshotVersion, chartInfoIndexVersion, hasResolvedSelection);
            PresentationIdentity = new PlaylistPresentationIdentity(keywordFilter, modeFilter, sortColumnName, sortDirection);
        }

        public bool Equals(PlaylistRequestIdentity other)
        {
            return SourceIdentity.Equals(other.SourceIdentity) && PresentationIdentity.Equals(other.PresentationIdentity);
        }

        public override bool Equals(object obj)
        {
            return obj is PlaylistRequestIdentity other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = SourceIdentity.GetHashCode();
                hashCode = (hashCode * 397) ^ PresentationIdentity.GetHashCode();
                return hashCode;
            }
        }

        public static bool operator ==(PlaylistRequestIdentity left, PlaylistRequestIdentity right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(PlaylistRequestIdentity left, PlaylistRequestIdentity right)
        {
            return !left.Equals(right);
        }
    }

    /// <summary>
    /// playlist source build で再利用するライブラリ索引 snapshot です。
    /// </summary>
    private sealed class PlaylistLibraryIndexSnapshot
    {
        internal long Version;

        internal long BuildElapsedMs;

        internal PlaylistLibraryResolveIndexSnapshot ResolveIndex = PlaylistLibraryResolveIndexSnapshot.Empty;
    }

    /// <summary>
    /// playlist open 要求時点の library index readiness を表します。
    /// </summary>
    private sealed class PlaylistLibraryIndexReadinessSnapshot
    {
        internal string State = "inline";

        internal long BuildElapsedMs;
    }

    /// <summary>
    /// playlist open 要求時点の readiness 情報です。
    /// </summary>
    private sealed class PlaylistOpenReadinessSnapshot
    {
        internal bool StartupReadyDataReached;

        internal bool StartupReadyUiReached;

        internal bool StartupReadyOperableReached;

        internal bool PlaylistRefDeferredRunning;

        internal int PlaylistRefDeferredLastCompletedVersion;

        internal bool MaintenanceHydrationRunning;

        internal int MaintenanceHydrationLastCompletedVersion;

        internal string PlaylistLibraryIndexState = "inline";

        internal long PlaylistLibraryIndexBuildMs;

        internal bool ScoreSnapshotReady;

        internal int ScoreSnapshotVersion;

        internal bool ScoreHydrationRunning;

        internal int ScoreHydrationCompletedVersion;

        internal bool RankingRefreshRunning;

        internal int RankingRefreshCompletedVersion;
    }
    /// <summary>
    /// playlist open 1 件の request/build/render 相関を保持します。
    /// </summary>
    private sealed class PlaylistOpenInteractionState
    {
        internal int RequestVersion;

        internal PlaylistRequestIdentity Identity;

        internal DateTime RequestedAtUtc;

        internal DateTime? BuildStartedAtUtc;

        internal DateTime? BuildCompletedAtUtc;

        internal long ExpectedSourceGenerationId;

        internal long ExpectedViewGenerationId;

        internal int ViewCount;

        internal bool VisibleCompletedLogged;

        internal PlaylistOpenReadinessSnapshot Readiness = new();
    }

    /// <summary>
    /// プレイリスト再読み込みの起点種別です。
    /// full reload 後 cleanup の対象判定とログ分類に利用します。
    /// </summary>
    private enum PlaylistReloadOperationKind
    {
        None,
        StartupFullReload,
        ManualFullReload,
        SinglePlaylistReload
    }

    /// <summary>
    /// プレイリスト再読み込み後 cleanup の pending 要求です。
    /// </summary>
    private sealed class PlaylistReloadCleanupRequest
    {
        internal long CleanupId;

        internal PlaylistReloadOperationKind OperationKind;

        internal int TableCount;

        internal bool WaitForStartupOperable;

        internal bool WaitForSummaryRefresh;

        internal bool WaitForDetailRefresh;

        internal bool GcAllowed;

        internal long RequestedAtTimestamp;
    }

    /// <summary>
    /// playlist score probe の集計メトリクスです。
    /// </summary>
    private sealed class PlaylistScoreProbeMetrics
    {
        internal int TargetCount;

        internal int MatchedScoreCount;

        internal long TotalMs;
    }

    private readonly struct PlaylistBuildRequestViewSnapshot
    {
        internal PlaylistBuildRequestViewSnapshot(long playlistRevision, int lastBuiltScoreSnapshotVersion, PlaylistRequestIdentity? currentViewIdentity)
        {
            PlaylistRevision = playlistRevision;
            LastBuiltScoreSnapshotVersion = lastBuiltScoreSnapshotVersion;
            CurrentViewIdentity = currentViewIdentity;
        }

        internal long PlaylistRevision { get; }

        internal int LastBuiltScoreSnapshotVersion { get; }

        internal PlaylistRequestIdentity? CurrentViewIdentity { get; }
    }

    /// <summary>
    /// プレイリスト詳細ビューの source snapshot を保持します。
    /// source は keyword/mode/sort 適用前の正本であり、表示更新は常にこの snapshot から再計算します。
    /// </summary>
    private sealed class PlaylistViewState
    {
        /// <summary>
        /// source snapshot の更新を直列化します。
        /// </summary>
        internal readonly object SyncRoot = new();

        /// <summary>
        /// 現在表示の正本となるプレイリスト行集合です。
        /// </summary>
        internal List<PlaylistDetailSourceRow> SourceRows = [];

        /// <summary>
        /// 現在一覧へ反映している表示用 snapshot です。
        /// source と別インスタンスで保持し、UI 側の retained reference と source 正本を切り分けます。
        /// </summary>
        internal IList CurrentViewRows = new List<object>();

        /// <summary>
        /// 現在表示中のプレイリストです。
        /// </summary>
        internal BMSTable CurrentTable;

        /// <summary>
        /// 現在表示中のプレイリストフォルダ名です。全体表示時は null です。
        /// </summary>
        internal string CurrentFolderName;

        /// <summary>
        /// 現在表示中のプレイリスト filter 種別です。
        /// </summary>
        internal PlaylistFilterType CurrentFilterType = PlaylistFilterType.PlaylistFilter;

        /// <summary>
        /// 現在の source snapshot 世代です。
        /// </summary>
        internal long SourceGenerationId;

        /// <summary>
        /// 現在採用中の view 世代です。
        /// </summary>
        internal long CurrentViewGenerationId;

        /// <summary>
        /// 現在採用中 view の件数です。
        /// </summary>
        internal int LastAppliedViewCount;

        /// <summary>
        /// 現在採用中 view の identity です。
        /// </summary>
        internal PlaylistRequestIdentity? CurrentViewIdentity;

        /// <summary>
        /// 現在採用中 source snapshot の identity です。
        /// keyword/mode/sort とは独立して、source rebuild 要否を判定します。
        /// </summary>
        internal PlaylistSourceIdentity? CurrentSourceIdentity;

        /// <summary>
        /// 直前に source build を完了した library index 版数です。
        /// </summary>
        internal long LastBuiltLibraryIndexVersion;

        /// <summary>
        /// 直前に source build を完了した playlist 更新版数です。
        /// </summary>
        internal long LastBuiltPlaylistRevision;

        /// <summary>
        /// 直前に source build を完了した score snapshot 版数です。
        /// </summary>
        internal int LastBuiltScoreSnapshotVersion;

        /// <summary>
        /// 直前に source build を完了した chart_info index 版数です。
        /// </summary>
        internal int LastBuiltChartInfoIndexVersion;

        /// <summary>
        /// playlist 内容更新版数です。
        /// </summary>
        internal long PlaylistContentRevision;

        /// <summary>
        /// playlist 行セルを現在編集中かどうかです。
        /// score snapshot 更新時の即時再構築抑止に利用します。
        /// </summary>
        internal bool IsPlaylistCellEditing;

        /// <summary>
        /// 編集中に保留した score snapshot refresh 版数です。
        /// </summary>
        internal int PendingScoreSnapshotRefreshVersion;

        /// <summary>
        /// 直前に置き換えた source snapshot の弱参照です。
        /// </summary>
        internal WeakReference<List<PlaylistDetailSourceRow>> PreviousSourceRowsWeakReference;

        /// <summary>
        /// 直前に置き換えた source snapshot 世代です。
        /// </summary>
        internal long PreviousSourceGenerationId;

        /// <summary>
        /// 直前に置き換えた view の弱参照です。
        /// </summary>
        internal WeakReference<IList> PreviousViewRowsWeakReference;

        /// <summary>
        /// 直前に置き換えた view 世代です。
        /// </summary>
        internal long PreviousViewGenerationId;

        /// <summary>
        /// 現在追跡中の playlist open interaction です。
        /// </summary>
        internal PlaylistOpenInteractionState CurrentOpenInteraction;
    }
}
