using System;

namespace BeMusicSeeker.ViewModels;

internal sealed class PlaylistOpenReadinessSnapshot
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

internal sealed class PlaylistOpenInteractionState
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
