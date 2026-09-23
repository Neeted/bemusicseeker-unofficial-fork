namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Counts owned chart rows by their current chart_info hydration state.
/// </summary>
internal sealed class ChartInfoHydrationOwnerSummary
{
    /// <summary>
    /// Gets or sets the number of owned chart rows that were classified.
    /// </summary>
    public int OwnerCount { get; set; }

    /// <summary>
    /// Gets or sets the number of owners that already have current chart_info rows.
    /// </summary>
    public int CurrentChartInfoOwnerCount { get; set; }

    /// <summary>
    /// Gets or sets the number of owners that already have current parse-failure rows.
    /// </summary>
    public int CurrentParseFailureOwnerCount { get; set; }

    /// <summary>
    /// Gets or sets the number of owners that still need chart_info backfill.
    /// </summary>
    public int BackfillCandidateOwnerCount { get; set; }

    /// <summary>
    /// Gets or sets the number of owners skipped because their chart_info rows are already current.
    /// </summary>
    public int OwnerApplySkippedCount { get; set; }
}
