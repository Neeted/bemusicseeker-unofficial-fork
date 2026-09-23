namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal readonly struct StorageRowsVersionSnapshot
{
    internal StorageRowsVersionSnapshot(int bmsRowsVersion, int bmsonRowsVersion)
        : this(bmsRowsVersion, bmsonRowsVersion, bmsRowsVersion, bmsonRowsVersion)
    {
    }

    internal StorageRowsVersionSnapshot(
        int previousBmsRowsVersion,
        int previousBmsonRowsVersion,
        int bmsRowsVersion,
        int bmsonRowsVersion)
    {
        PreviousBmsRowsVersion = previousBmsRowsVersion;
        PreviousBmsonRowsVersion = previousBmsonRowsVersion;
        BmsRowsVersion = bmsRowsVersion;
        BmsonRowsVersion = bmsonRowsVersion;
    }

    internal int PreviousBmsRowsVersion { get; }

    internal int PreviousBmsonRowsVersion { get; }

    internal int BmsRowsVersion { get; }

    internal int BmsonRowsVersion { get; }
}
