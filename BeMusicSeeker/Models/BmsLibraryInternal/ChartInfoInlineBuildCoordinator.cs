using System;
using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Coordinates inline chart info build and persistence without owning BMSLibrary storage state.
/// </summary>
internal sealed class ChartInfoInlineBuildCoordinator
{
    private readonly IChartInfoInlineBuildHost host;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChartInfoInlineBuildCoordinator"/> class.
    /// </summary>
    /// <param name="host">The host that owns BMSLibrary private state.</param>
    internal ChartInfoInlineBuildCoordinator(IChartInfoInlineBuildHost host)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>
    /// Builds and persists inline chart info rows while preserving digest dispatch fallback behavior.
    /// </summary>
    /// <param name="reason">The requested build reason.</param>
    /// <param name="charts">The requested target charts.</param>
    /// <returns>The inline build result.</returns>
    internal ChartInfoInlineBuildResult BuildAndPersist(
        string reason,
        IEnumerable<ChartFile> charts)
    {
        List<ChartFile> targetCharts = host.NormalizeTargetCharts(charts);
        ChartStorageTargetSet storageTargets = ChartStorageTargetSet.FromCharts(targetCharts);
        var result = new ChartInfoInlineBuildResult();
        bool completed = false;
        if (targetCharts.Count == 0)
        {
            host.LogEmptyResult(reason);
            return result;
        }

        using (host.BeginOwnedDigestMutationWindow())
        {
            try
            {
                result = host.BuildForExistingCharts(targetCharts);
                int songRowChartInfoApplied = host.ApplyChartInfoRowsToBmsStorageRows(
                    storageTargets.BmsFiles,
                    result.AppliedRows);
                if (storageTargets.BmsFiles.Count > 0)
                {
                    host.UpsertBmsStorageRows(storageTargets.BmsFiles, reason);
                }
                if (storageTargets.BmsonSongs.Count > 0)
                {
                    host.UpsertBmsonStorageRows(storageTargets.BmsonSongs);
                }
                host.UpsertChartInfoBackfillChunk(result);
                if (result.AppliedRows.Count > 0)
                {
                    host.UpsertChartInfoIndexRows(result.AppliedRows, reason ?? "install_package_inline");
                }
                if (result.ParseFailureRows.Count > 0 || result.ParseFailureDeleteMd5s.Count > 0)
                {
                    host.DispatchWarningPresentationChanged("install_package_inline_chart_info_parse_failure");
                }
                completed = true;
                host.LogCompletedResult(reason, result, songRowChartInfoApplied);
                return result;
            }
            finally
            {
                if (completed)
                {
                    host.DispatchOwnedChartDigestChanges(result.DigestChanges, reason ?? "install_package_inline");
                }
                else
                {
                    host.DispatchOwnedPotentialDigestChanges(targetCharts, (reason ?? "install_package_inline") + "_failed");
                }
            }
        }
    }
}
