namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class FileScanDiffCommitMetrics
{
    public long SchemaMs { get; set; }

    public long BmsDeleteMs { get; set; }

    public long BmsPathCaseUpdateMs { get; set; }

    public long BmsDateUpdateMs { get; set; }

    public long BmsUpsertMs { get; set; }

    public int BmsChangedCount { get; set; }

    public long BmsonDeleteMs { get; set; }

    public long BmsonPathCaseUpdateMs { get; set; }

    public long BmsonUpsertMs { get; set; }

    public long MaintenanceUpsertMs { get; set; }

    public long ChartInfoMs { get; set; }

    public long ApplyMs => SchemaMs
        + BmsDeleteMs
        + BmsPathCaseUpdateMs
        + BmsDateUpdateMs
        + BmsUpsertMs
        + BmsonDeleteMs
        + BmsonPathCaseUpdateMs
        + BmsonUpsertMs
        + MaintenanceUpsertMs
        + ChartInfoMs;

    public void AddFrom(FileScanDiffCommitMetrics source)
    {
        if (source == null)
        {
            return;
        }
        SchemaMs += source.SchemaMs;
        BmsDeleteMs += source.BmsDeleteMs;
        BmsPathCaseUpdateMs += source.BmsPathCaseUpdateMs;
        BmsDateUpdateMs += source.BmsDateUpdateMs;
        BmsUpsertMs += source.BmsUpsertMs;
        BmsChangedCount += source.BmsChangedCount;
        BmsonDeleteMs += source.BmsonDeleteMs;
        BmsonPathCaseUpdateMs += source.BmsonPathCaseUpdateMs;
        BmsonUpsertMs += source.BmsonUpsertMs;
        MaintenanceUpsertMs += source.MaintenanceUpsertMs;
        ChartInfoMs += source.ChartInfoMs;
    }
}
