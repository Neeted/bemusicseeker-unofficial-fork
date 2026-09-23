using System;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

internal enum PlaylistDetailBuildAction
{
    RebuildSource,
    PatchChartInfoThenApplyView,
    ApplyViewOnly
}

internal readonly struct PlaylistDetailBuildStateSnapshot
{
    internal PlaylistDetailBuildStateSnapshot(
        bool hasSourceRows,
        int sourceRowCount,
        BMSTable currentTable,
        PlaylistDetailSelectionScope currentSelectionScope,
        string currentFolderName,
        PlaylistDetailFilter currentFilterType,
        long lastBuiltLibraryIndexVersion,
        long lastBuiltPlaylistRevision,
        int lastBuiltScoreSnapshotVersion,
        int lastBuiltChartInfoIndexVersion,
        PlaylistSourceIdentity? currentSourceIdentity,
        PlaylistRequestIdentity? currentViewIdentity)
    {
        HasSourceRows = hasSourceRows;
        SourceRowCount = sourceRowCount;
        CurrentTable = currentTable;
        CurrentSelectionScope = currentSelectionScope;
        CurrentFolderName = currentFolderName;
        CurrentFilterType = currentFilterType;
        LastBuiltLibraryIndexVersion = lastBuiltLibraryIndexVersion;
        LastBuiltPlaylistRevision = lastBuiltPlaylistRevision;
        LastBuiltScoreSnapshotVersion = lastBuiltScoreSnapshotVersion;
        LastBuiltChartInfoIndexVersion = lastBuiltChartInfoIndexVersion;
        CurrentSourceIdentity = currentSourceIdentity;
        CurrentViewIdentity = currentViewIdentity;
    }

    internal bool HasSourceRows { get; }

    internal int SourceRowCount { get; }

    internal BMSTable CurrentTable { get; }

    internal PlaylistDetailSelectionScope CurrentSelectionScope { get; }

    internal string CurrentFolderName { get; }

    internal PlaylistDetailFilter CurrentFilterType { get; }

    internal long LastBuiltLibraryIndexVersion { get; }

    internal long LastBuiltPlaylistRevision { get; }

    internal int LastBuiltScoreSnapshotVersion { get; }

    internal int LastBuiltChartInfoIndexVersion { get; }

    internal PlaylistSourceIdentity? CurrentSourceIdentity { get; }

    internal PlaylistRequestIdentity? CurrentViewIdentity { get; }
}

internal readonly struct PlaylistDetailBuildDecision
{
    internal PlaylistDetailBuildDecision(
        PlaylistDetailBuildAction action,
        bool selectionChanged,
        bool libraryIndexInvalidated,
        bool playlistRevisionInvalidated,
        bool scoreSnapshotInvalidated,
        bool chartInfoIndexInvalidated,
        bool sourceMissing,
        bool presentationIdentityChanged,
        string sourceInvalidationReason,
        int lastBuiltScoreSnapshotVersion)
    {
        Action = action;
        SelectionChanged = selectionChanged;
        LibraryIndexInvalidated = libraryIndexInvalidated;
        PlaylistRevisionInvalidated = playlistRevisionInvalidated;
        ScoreSnapshotInvalidated = scoreSnapshotInvalidated;
        ChartInfoIndexInvalidated = chartInfoIndexInvalidated;
        SourceMissing = sourceMissing;
        PresentationIdentityChanged = presentationIdentityChanged;
        SourceInvalidationReason = sourceInvalidationReason;
        LastBuiltScoreSnapshotVersion = lastBuiltScoreSnapshotVersion;
    }

    internal PlaylistDetailBuildAction Action { get; }

    internal bool SelectionChanged { get; }

    internal bool LibraryIndexInvalidated { get; }

    internal bool PlaylistRevisionInvalidated { get; }

    internal bool ScoreSnapshotInvalidated { get; }

    internal bool ChartInfoIndexInvalidated { get; }

    internal bool SourceMissing { get; }

    internal bool PresentationIdentityChanged { get; }

    internal string SourceInvalidationReason { get; }

    internal int LastBuiltScoreSnapshotVersion { get; }
}

internal static class PlaylistDetailBuildDecisionService
{
    internal static PlaylistDetailBuildDecision Decide(PlaylistBuildRequest request, PlaylistDetailBuildStateSnapshot snapshot)
    {
        if (request == null)
        {
            return new PlaylistDetailBuildDecision(
                PlaylistDetailBuildAction.ApplyViewOnly,
                selectionChanged: false,
                libraryIndexInvalidated: false,
                playlistRevisionInvalidated: false,
                scoreSnapshotInvalidated: false,
                chartInfoIndexInvalidated: false,
                sourceMissing: false,
                presentationIdentityChanged: false,
                sourceInvalidationReason: null,
                snapshot.LastBuiltScoreSnapshotVersion);
        }

        bool hasResolvedPlaylistSource = snapshot.CurrentSourceIdentity.HasValue
            || snapshot.CurrentTable != null
            || snapshot.CurrentFilterType == PlaylistDetailFilter.PlaylistNotOwnedFilterSelected;
        bool selectionChanged = !snapshot.CurrentSourceIdentity.HasValue
            || snapshot.CurrentTable != request.Identity.Table
            || snapshot.CurrentSelectionScope != request.Identity.SelectionScope
            || !string.Equals(PlaylistRequestFactory.NormalizeFolderName(snapshot.CurrentFolderName), request.Identity.FolderName, StringComparison.Ordinal)
            || snapshot.CurrentFilterType != request.Identity.FilterType
            || snapshot.CurrentSourceIdentity.Value.HasResolvedSelection != request.Identity.HasResolvedSelection;
        bool libraryIndexInvalidated = snapshot.LastBuiltLibraryIndexVersion != request.Identity.LibraryIndexVersion;
        bool playlistRevisionInvalidated = snapshot.LastBuiltPlaylistRevision != request.Identity.PlaylistRevision;
        bool scoreSnapshotInvalidated = snapshot.LastBuiltScoreSnapshotVersion != request.Identity.ScoreSnapshotVersion;
        bool chartInfoIndexInvalidated = snapshot.LastBuiltChartInfoIndexVersion != request.Identity.ChartInfoIndexVersion;
        bool sourceIdentityInvalidatedExceptChartInfo = !snapshot.CurrentSourceIdentity.HasValue
            || !snapshot.CurrentSourceIdentity.Value.EqualsIgnoringChartInfoIndex(request.Identity.SourceIdentity);
        bool presentationIdentityChanged = !snapshot.CurrentViewIdentity.HasValue
            || !snapshot.CurrentViewIdentity.Value.PresentationIdentity.Equals(request.Identity.PresentationIdentity);
        bool sourceMissing = !snapshot.HasSourceRows || (snapshot.SourceRowCount == 0 && !hasResolvedPlaylistSource);
        bool sourceInvalidated = sourceIdentityInvalidatedExceptChartInfo
            || libraryIndexInvalidated
            || playlistRevisionInvalidated
            || scoreSnapshotInvalidated;
        bool chartInfoOnlyInvalidated = chartInfoIndexInvalidated
            && !selectionChanged
            && !sourceInvalidated
            && !sourceMissing;
        if (chartInfoOnlyInvalidated)
        {
            return new PlaylistDetailBuildDecision(
                PlaylistDetailBuildAction.PatchChartInfoThenApplyView,
                selectionChanged,
                libraryIndexInvalidated,
                playlistRevisionInvalidated,
                scoreSnapshotInvalidated,
                chartInfoIndexInvalidated,
                sourceMissing,
                presentationIdentityChanged,
                sourceInvalidationReason: null,
                snapshot.LastBuiltScoreSnapshotVersion);
        }

        sourceInvalidated = sourceInvalidated || (chartInfoIndexInvalidated && !chartInfoOnlyInvalidated);
        bool requiresSourceRebuild = selectionChanged || sourceInvalidated || sourceMissing;
        if (requiresSourceRebuild)
        {
            return new PlaylistDetailBuildDecision(
                PlaylistDetailBuildAction.RebuildSource,
                selectionChanged,
                libraryIndexInvalidated,
                playlistRevisionInvalidated,
                scoreSnapshotInvalidated,
                chartInfoIndexInvalidated,
                sourceMissing,
                presentationIdentityChanged,
                PlaylistRequestFactory.DetermineSourceInvalidationReason(
                    selectionChanged,
                    libraryIndexInvalidated,
                    playlistRevisionInvalidated,
                    scoreSnapshotInvalidated,
                    chartInfoIndexInvalidated,
                    sourceMissing),
                snapshot.LastBuiltScoreSnapshotVersion);
        }

        return new PlaylistDetailBuildDecision(
            PlaylistDetailBuildAction.ApplyViewOnly,
            selectionChanged,
            libraryIndexInvalidated,
            playlistRevisionInvalidated,
            scoreSnapshotInvalidated,
            chartInfoIndexInvalidated,
            sourceMissing,
            presentationIdentityChanged,
            sourceInvalidationReason: null,
            snapshot.LastBuiltScoreSnapshotVersion);
    }
}
