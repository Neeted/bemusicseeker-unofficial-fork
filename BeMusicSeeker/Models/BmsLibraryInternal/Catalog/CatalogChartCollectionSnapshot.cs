using System;
using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>共通現在値の所属・順序・集合版を同時に捕捉した読取り専用ビューです。</summary>
internal sealed class CatalogChartCollectionSnapshot
{
    /// <summary>捕捉した順序列と集合版を結び付けます。</summary>
    /// <param name="bmsRows">捕捉したBMS現在値のビュー。</param>
    /// <param name="bmsonRows">捕捉したBMSON現在値のビュー。</param>
    /// <param name="ownedCollectionVersion">捕捉時点の共通集合版。</param>
    internal CatalogChartCollectionSnapshot(IReadOnlyList<ChartFile> bmsRows, IReadOnlyList<ChartFile> bmsonRows, int ownedCollectionVersion)
    {
        BmsRows = bmsRows ?? Array.Empty<ChartFile>();
        BmsonRows = bmsonRows ?? Array.Empty<ChartFile>();
        OwnedCollectionVersion = ownedCollectionVersion;

    }

    /// <summary>捕捉時のBMSの所属・順序・現在値です。</summary>
    internal IReadOnlyList<ChartFile> BmsRows { get; }

    /// <summary>捕捉時のBMSONの所属・順序・現在値です。</summary>
    internal IReadOnlyList<ChartFile> BmsonRows { get; }

    /// <summary>捕捉時点の共通集合版です。</summary>
    internal int OwnedCollectionVersion { get; }


}

/// <summary>共通集合版と形式別の件数を保持する軽量な状態値です。</summary>
internal readonly struct CatalogChartCollectionStateSnapshot
{
    /// <summary>共通集合版と件数を結び付けます。</summary>
    /// <param name="ownedCollectionVersion">共通集合版。</param>
    /// <param name="bmsRowCount">BMSの件数。</param>
    /// <param name="bmsonRowCount">BMSONの件数。</param>
    internal CatalogChartCollectionStateSnapshot(int ownedCollectionVersion, int bmsRowCount, int bmsonRowCount)
    {
        OwnedCollectionVersion = ownedCollectionVersion;

        BmsRowCount = bmsRowCount;
        BmsonRowCount = bmsonRowCount;
    }

    /// <summary>捕捉時点の共通集合版です。</summary>
    internal int OwnedCollectionVersion { get; }



    /// <summary>BMSの件数。</summary>
    internal int BmsRowCount { get; }

    /// <summary>BMSONの件数。</summary>
    internal int BmsonRowCount { get; }
}
