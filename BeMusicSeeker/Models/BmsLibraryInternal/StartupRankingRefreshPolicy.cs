using System;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Describes the independent pieces of deferred startup ranking refresh work.
/// </summary>
/// <param name="RefreshIrScore">Whether the LR2IR player score table should be refreshed.</param>
/// <param name="RefreshRankingCache">Whether the LR2IR ranking cache should be refreshed.</param>
internal readonly record struct StartupRankingRefreshWorkPlan(
    bool RefreshIrScore,
    bool RefreshRankingCache)
{
    /// <summary>
    /// Gets whether at least one ranking refresh operation is enabled.
    /// </summary>
    public bool HasWork => RefreshIrScore || RefreshRankingCache;
}

/// <summary>
/// Evaluates the pure startup ranking refresh queue contract.
/// </summary>
internal static class StartupRankingRefreshPolicy
{
    /// <summary>
    /// Creates the refresh work plan represented by one immutable options snapshot.
    /// </summary>
    /// <param name="options">Options captured for the queue or execution boundary.</param>
    /// <returns>The independent IR score and ranking-cache work flags.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public static StartupRankingRefreshWorkPlan CreateWorkPlan(BmsLibraryOptionsSnapshot options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new(
            options.EnableDownloadLr2IrScoreAndDetectUnsent,
            options.UpdateLr2IrRankingCacheOnStartup);
    }

    /// <summary>
    /// Determines whether deferred startup ranking refresh should be queued.
    /// </summary>
    /// <param name="updateRequested">Whether the current initialization requested score updates.</param>
    /// <param name="activeScoreSource">The score source loaded by initialization.</param>
    /// <param name="hasLr2ScoreDatabase">Whether an LR2 score database path is configured.</param>
    /// <param name="plan">The work plan captured at the queue boundary.</param>
    /// <returns><see langword="true"/> only when the existing queue contract is satisfied.</returns>
    public static bool ShouldQueue(
        bool updateRequested,
        ActiveScoreSource activeScoreSource,
        bool hasLr2ScoreDatabase,
        StartupRankingRefreshWorkPlan plan)
    {
        return updateRequested
            && activeScoreSource == ActiveScoreSource.Lr2
            && hasLr2ScoreDatabase
            && plan.HasWork;
    }
}
