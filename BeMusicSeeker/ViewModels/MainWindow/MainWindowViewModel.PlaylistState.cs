using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

internal enum PlaylistDetailFilter
{
    PlaylistFilter = 1,
    PlaylistNotOwnedFilterSelected
}

public partial class MainWindowViewModel
{
    /// <summary>
    /// Compatibility name for the former nested playlist filter.
    /// </summary>
    public enum PlaylistFilterType
    {
        PlaylistFilter = 1,
        PlaylistNotOwnedFilterSelected
    }
}

/// <summary>
/// playlist source snapshot の再構築要否を判定する正規化済み snapshot です。
/// </summary>
internal readonly struct PlaylistSourceIdentity : IEquatable<PlaylistSourceIdentity>
{
    internal BMSTable Table { get; }

    internal string FolderName { get; }

    internal PlaylistDetailFilter FilterType { get; }

    internal long LibraryIndexVersion { get; }

    internal long PlaylistRevision { get; }

    internal int ScoreSnapshotVersion { get; }

    internal int ChartInfoIndexVersion { get; }

    internal bool HasResolvedSelection { get; }

    internal PlaylistSourceIdentity(BMSTable table, string folderName, PlaylistDetailFilter filterType, long libraryIndexVersion, long playlistRevision, int scoreSnapshotVersion, int chartInfoIndexVersion, bool hasResolvedSelection)
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

    internal ChartModeFilter ModeFilter { get; }

    internal string SortColumnName { get; }

    internal ListSortDirection SortDirection { get; }

    internal PlaylistPresentationIdentity(string keywordFilter, ChartModeFilter modeFilter, string sortColumnName, ListSortDirection sortDirection)
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

    internal PlaylistDetailFilter FilterType => SourceIdentity.FilterType;

    internal string KeywordFilter => PresentationIdentity.KeywordFilter;

    internal ChartModeFilter ModeFilter => PresentationIdentity.ModeFilter;

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
    internal PlaylistRequestIdentity(BMSTable table, string folderName, PlaylistDetailFilter filterType, string keywordFilter, ChartModeFilter modeFilter, string sortColumnName, ListSortDirection sortDirection, long libraryIndexVersion, long playlistRevision, int scoreSnapshotVersion, int chartInfoIndexVersion, bool hasResolvedSelection)
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
internal sealed class PlaylistLibraryIndexSnapshot
{
    internal long Version;

    internal long BuildElapsedMs;

    internal PlaylistLibraryResolveIndexSnapshot ResolveIndex = PlaylistLibraryResolveIndexSnapshot.Empty;
}

public partial class MainWindowViewModel
{
    /// <summary>
    /// playlist open 要求時点の library index readiness を表します。
    /// </summary>
    private sealed class PlaylistLibraryIndexReadinessSnapshot
    {
        internal string State = "inline";

        internal long BuildElapsedMs;
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

}
