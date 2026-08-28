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

/// <summary>
/// Identifies the semantic source scope represented by a playlist-detail selection.
/// The scope is part of build identity because an overall lamp navigation and the
/// ordinary playlist root both use a null folder name while exposing different rows.
/// </summary>
internal enum PlaylistDetailSelectionScope
{
    /// <summary>The ordinary playlist root, including its legacy special-folder rows.</summary>
    OrdinaryRoot = 1,

    /// <summary>A specific playlist folder, including the not-owned special folder.</summary>
    Folder,

    /// <summary>All current normal folders for an overall lamp-viewer navigation.</summary>
    OverallNormalFolders
}

/// <summary>
/// playlist source snapshot の再構築要否を判定する正規化済み snapshot です。
/// </summary>
internal readonly struct PlaylistSourceIdentity : IEquatable<PlaylistSourceIdentity>
{
    internal BMSTable Table { get; }

    internal PlaylistDetailSelectionScope SelectionScope { get; }

    internal string FolderName { get; }

    internal PlaylistDetailFilter FilterType { get; }

    internal long LibraryIndexVersion { get; }

    internal long PlaylistRevision { get; }

    internal int ScoreSnapshotVersion { get; }

    internal int ChartInfoIndexVersion { get; }

    internal bool HasResolvedSelection { get; }

    internal PlaylistSourceIdentity(BMSTable table, PlaylistDetailSelectionScope selectionScope, string folderName, PlaylistDetailFilter filterType, long libraryIndexVersion, long playlistRevision, int scoreSnapshotVersion, int chartInfoIndexVersion, bool hasResolvedSelection)
    {
        Table = table;
        SelectionScope = selectionScope;
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
        return Table == other.Table && SelectionScope == other.SelectionScope && string.Equals(FolderName, other.FolderName, StringComparison.Ordinal) && FilterType == other.FilterType && LibraryIndexVersion == other.LibraryIndexVersion && PlaylistRevision == other.PlaylistRevision && ScoreSnapshotVersion == other.ScoreSnapshotVersion && ChartInfoIndexVersion == other.ChartInfoIndexVersion && HasResolvedSelection == other.HasResolvedSelection;
    }

    internal bool EqualsIgnoringChartInfoIndex(PlaylistSourceIdentity other)
    {
        return Table == other.Table && SelectionScope == other.SelectionScope && string.Equals(FolderName, other.FolderName, StringComparison.Ordinal) && FilterType == other.FilterType && LibraryIndexVersion == other.LibraryIndexVersion && PlaylistRevision == other.PlaylistRevision && ScoreSnapshotVersion == other.ScoreSnapshotVersion && HasResolvedSelection == other.HasResolvedSelection;
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
            hashCode = (hashCode * 397) ^ (int)SelectionScope;
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

    internal PlaylistDetailSelectionScope SelectionScope => SourceIdentity.SelectionScope;

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
    internal PlaylistRequestIdentity(BMSTable table, PlaylistDetailSelectionScope selectionScope, string folderName, PlaylistDetailFilter filterType, string keywordFilter, ChartModeFilter modeFilter, string sortColumnName, ListSortDirection sortDirection, long libraryIndexVersion, long playlistRevision, int scoreSnapshotVersion, int chartInfoIndexVersion, bool hasResolvedSelection)
    {
        SourceIdentity = new PlaylistSourceIdentity(table, selectionScope, folderName, filterType, libraryIndexVersion, playlistRevision, scoreSnapshotVersion, chartInfoIndexVersion, hasResolvedSelection);
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

public partial class MainWindowViewModel
{
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
