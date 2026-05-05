namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class IrCacheRefreshResult
{
    public long ElapsedMs { get; set; }

    public long DbReadMs { get; set; }

    public int DbRows { get; set; }

    public long IndexBuildMs { get; set; }

    public int CacheFilesScanned { get; set; }

    public long XmlCheckMs { get; set; }

    public int CacheFilesReloaded { get; set; }

    public long XmlReloadMs { get; set; }

    public int IrDataUpsertCount { get; set; }

    public long UpsertMs { get; set; }

    public int DbFallbackAppliedCount { get; set; }

    public int XmlAppliedCount { get; set; }

    public int OfflineEstimateXmlLoadCount { get; set; }
}
