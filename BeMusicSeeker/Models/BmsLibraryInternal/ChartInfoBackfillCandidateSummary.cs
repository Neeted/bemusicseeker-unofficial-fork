namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ChartInfoBackfillCandidateSummary
{
    public int BmsOwnerCount { get; set; }

    public int BmsonOwnerCount { get; set; }

    public int MissingDigestOwnerCount { get; set; }

    public int MissingChartInfoOwnerCount { get; set; }

    public int StaleChartInfoOwnerCount { get; set; }

    public int CurrentParseFailureOwnerCount { get; set; }

    public int CurrentChartInfoOwnerCount { get; set; }

    public int CandidateOwnerCount { get; set; }
}
