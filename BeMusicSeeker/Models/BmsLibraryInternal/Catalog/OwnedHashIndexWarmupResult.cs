namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class OwnedHashIndexWarmupResult
{
    internal string IndexName { get; set; }

    internal string Status { get; set; }

    internal long ElapsedMs { get; set; }

    internal int Md5Count { get; set; }

    internal int Sha256Count { get; set; }

    internal int SnapshotVersion { get; set; }

    internal int InvalidationVersion { get; set; }

    internal int OwnedCollectionVersion { get; set; }

    internal int BmsRowsVersion { get; set; }

    internal int BmsonRowsVersion { get; set; }

    internal int StaleRetryCount { get; set; }
}
