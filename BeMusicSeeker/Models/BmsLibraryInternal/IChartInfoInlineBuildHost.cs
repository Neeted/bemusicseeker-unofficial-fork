using System;
using System.Collections.Generic;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Bridges inline chart info build and persistence to BMSLibrary state that must remain private.
/// </summary>
internal interface IChartInfoInlineBuildHost
{
    /// <summary>
    /// Normalizes the target charts for resource maintenance.
    /// </summary>
    /// <param name="charts">The requested target charts.</param>
    /// <returns>The normalized chart list.</returns>
    List<ChartFile> NormalizeTargetCharts(IEnumerable<ChartFile> charts);

    /// <summary>
    /// Logs the zero-target inline chart info result.
    /// </summary>
    /// <param name="reason">The requested build reason.</param>
    void LogEmptyResult(string reason);

    /// <summary>
    /// Begins the owned digest mutation window.
    /// </summary>
    /// <returns>The active mutation window scope.</returns>
    IDisposable BeginOwnedDigestMutationWindow();

    /// <summary>
    /// Builds chart info rows for existing installed charts.
    /// </summary>
    /// <param name="targetCharts">The normalized target charts.</param>
    /// <returns>The inline build result.</returns>
    ChartInfoInlineBuildResult BuildForExistingCharts(List<ChartFile> targetCharts);

    /// <summary>
    /// Applies chart info columns to BMS storage row objects.
    /// </summary>
    /// <param name="bmsFiles">The BMS storage row objects.</param>
    /// <param name="chartInfoRows">The chart info rows to apply.</param>
    /// <returns>The number of BMS storage rows changed.</returns>
    int ApplyChartInfoRowsToBmsStorageRows(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.chart_info> chartInfoRows);

    /// <summary>
    /// Upserts changed BMS storage rows into the LR2 song DB.
    /// </summary>
    /// <param name="bmsFiles">The BMS rows to persist.</param>
    /// <param name="reason">The requested build reason.</param>
    void UpsertBmsStorageRows(IReadOnlyCollection<BMSFile> bmsFiles, string reason);

    /// <summary>
    /// Upserts changed bmson storage rows into the LR2 song DB.
    /// </summary>
    /// <param name="bmsonSongs">The bmson rows to persist.</param>
    void UpsertBmsonStorageRows(IReadOnlyCollection<LR2SongDBExtended.bmson_song> bmsonSongs);

    /// <summary>
    /// Persists chart info backfill rows and parse failure rows.
    /// </summary>
    /// <param name="result">The inline build result to persist.</param>
    void UpsertChartInfoBackfillChunk(ChartInfoInlineBuildResult result);

    /// <summary>
    /// Upserts chart info index rows created by inline build.
    /// </summary>
    /// <param name="appliedRows">The rows applied to storage rows.</param>
    /// <param name="reason">The index update reason.</param>
    void UpsertChartInfoIndexRows(
        IReadOnlyCollection<LR2SongDBExtended.chart_info> appliedRows,
        string reason);

    /// <summary>
    /// Dispatches warning presentation changes when parse failure rows changed.
    /// </summary>
    /// <param name="reason">The warning presentation reason.</param>
    void DispatchWarningPresentationChanged(string reason);

    /// <summary>
    /// Logs the completed inline chart info result.
    /// </summary>
    /// <param name="reason">The requested build reason.</param>
    /// <param name="result">The completed inline build result.</param>
    /// <param name="songRowChartInfoApplied">The number of storage rows with chart info applied.</param>
    void LogCompletedResult(
        string reason,
        ChartInfoInlineBuildResult result,
        int songRowChartInfoApplied);

    /// <summary>
    /// Dispatches owned digest changes after a successful inline build.
    /// </summary>
    /// <param name="changes">The digest changes to dispatch.</param>
    /// <param name="reason">The dispatch reason.</param>
    void DispatchOwnedChartDigestChanges(
        IReadOnlyCollection<LibraryChartDigestChange> changes,
        string reason);

    /// <summary>
    /// Dispatches potential digest changes when inline build fails before completion.
    /// </summary>
    /// <param name="targetCharts">The target charts that may have changed.</param>
    /// <param name="reason">The dispatch reason.</param>
    void DispatchOwnedPotentialDigestChanges(List<ChartFile> targetCharts, string reason);
}
