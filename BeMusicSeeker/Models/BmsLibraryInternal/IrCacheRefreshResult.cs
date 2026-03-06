namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class IrCacheRefreshResult
{
    public int CacheFilesScanned { get; set; }

    public int CacheFilesReloaded { get; set; }

    public int IrDataUpsertCount { get; set; }

    public int DbFallbackAppliedCount { get; set; }
}
