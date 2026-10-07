using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2SongDbSyncInputRowSnapshot(IReadOnlyList<string> chartPaths, IReadOnlyList<ChartFile> songRows, int ownedCollectionVersion, IReadOnlyDictionary<string, OwnedChartToken> pathMembershipIndex = null)
{
    public IReadOnlyList<string> ChartPaths { get; } = chartPaths ?? [];

    public IReadOnlyList<ChartFile> SongRows { get; } = songRows ?? [];

    public int OwnedCollectionVersion { get; } = ownedCollectionVersion;


    /// <summary>同じ捕捉時点の所属・DB exact path索引です。表示値の差替えでは共有されます。</summary>
    internal IReadOnlyDictionary<string, OwnedChartToken> PathMembershipIndex { get; } = pathMembershipIndex;
}
