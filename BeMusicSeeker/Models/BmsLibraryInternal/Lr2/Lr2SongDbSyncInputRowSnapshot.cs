using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2SongDbSyncInputRowSnapshot(
    IReadOnlyList<string> chartPaths,
    IReadOnlyList<BMSFile> songRows,
    int ownedCollectionVersion,
    int bmsRowsVersion,
    int bmsonRowsVersion)
{
    public IReadOnlyList<string> ChartPaths { get; } = chartPaths ?? [];

    public IReadOnlyList<BMSFile> SongRows { get; } = songRows ?? [];

    public int OwnedCollectionVersion { get; } = ownedCollectionVersion;

    public int BmsRowsVersion { get; } = bmsRowsVersion;

    public int BmsonRowsVersion { get; } = bmsonRowsVersion;
}
