using System;
using System.Collections.Generic;
using System.Threading;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>共通現在値の所属・順序・集合版を同時に捕捉した読取り専用ビューです。</summary>
internal sealed class CatalogChartCollectionSnapshot
{
    private readonly OwnedChartCollectionView collectionView;
    private readonly PlaylistLibraryResolveIndexSnapshot playlistResolveIndex;
    /// <summary>捕捉した順序列と集合版を結び付けます。</summary>
    /// <param name="bmsRows">捕捉したBMS現在値のビュー。</param>
    /// <param name="bmsonRows">捕捉したBMSON現在値のビュー。</param>
    /// <param name="catalogRevision">捕捉時点の共通集合版。</param>
    /// <param name="membershipOrderIdentity">所属と基準順序の共有識別。</param>
    /// <param name="collectionView">token現在値と基準位置を持つ同じ不変root。</param>
    /// <param name="playlistResolveIndex">捕捉時にwarmな既存候補root。coldならnullで構築しません。</param>
    internal CatalogChartCollectionSnapshot(IReadOnlyList<ChartFile> bmsRows, IReadOnlyList<ChartFile> bmsonRows, int catalogRevision,
        object membershipOrderIdentity = null, OwnedChartCollectionView collectionView = null,
        PlaylistLibraryResolveIndexSnapshot playlistResolveIndex = null)
    {
        BmsRows = bmsRows ?? Array.Empty<ChartFile>();
        BmsonRows = bmsonRows ?? Array.Empty<ChartFile>();
        CatalogRevision = catalogRevision;
        MembershipOrderIdentity = membershipOrderIdentity ?? new object();
        this.collectionView = collectionView;
        this.playlistResolveIndex = playlistResolveIndex;
    }

    /// <summary>捕捉済みの候補rootと現在値から必要keyだけを解決します。後続のowner現在値を取得しません。</summary>
    /// <param name="keys">必要なMD5/SHA-256候補key。hashの大文字小文字を区別しません。</param>
    /// <param name="snapshot">同じ確定版の候補と現在値。候補未所持はResolveがnullを返します。</param>
    /// <param name="cancellationToken">必要keyの解決中に確認する取消し。</param>
    /// <returns>捕捉時に候補索引がwarmならtrue。cold・全置換直後は構築せずfalse。</returns>
    internal bool TryCapturePlaylistReadSnapshot(IEnumerable<PlaylistEntryLookupKey> keys, out CatalogReadSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        snapshot = null;
        if (playlistResolveIndex == null)
        {
            return false;
        }
        var resolved = new Dictionary<PlaylistEntryLookupKey, LibraryChartRef>(CatalogReadSnapshot.LookupKeyComparer);
        var charts = new Dictionary<OwnedChartToken, ChartFile>();
        foreach (PlaylistEntryLookupKey key in keys ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            LibraryChartRef reference = playlistResolveIndex.ResolveChartForPlaylistLookupKey(key);
            resolved[key] = reference;
            if (reference?.Token != null && TryGetChart(reference.Token, out ChartFile chart, out _))
            {
                charts[reference.Token] = chart;
            }
        }
        snapshot = new(CatalogRevision, charts, resolved);
        return true;
    }

    /// <summary>捕捉時のBMSの所属・順序・現在値です。</summary>
    internal IReadOnlyList<ChartFile> BmsRows { get; }

    /// <summary>捕捉時のBMSONの所属・順序・現在値です。</summary>
    internal IReadOnlyList<ChartFile> BmsonRows { get; }

    /// <summary>捕捉時点の共通集合版です。</summary>
    internal int CatalogRevision { get; }

    /// <summary>所属と基準順序が変わると差し替わる識別です。基本値の版とは独立して添字再利用を判断します。</summary>
    internal object MembershipOrderIdentity { get; }

    /// <summary>捕捉済みの既存exact path索引で表示行cacheの所属を検査します。行getterの現在値解決には使いません。</summary>
    internal bool ContainsChart(ChartFile chart) => collectionView?.ContainsChart(chart) == true;
    /// <summary>捕捉した同じ不変rootから、tokenの現在値とBMS/BMSON連結順の位置を対数時間で取得します。</summary>
    internal bool TryGetChart(OwnedChartToken token, out ChartFile chart, out int canonicalPosition)
    {
        chart = null;
        canonicalPosition = -1;
        return collectionView?.TryGetChart(token, out chart, out canonicalPosition) == true;
    }



}

/// <summary>共通集合版と形式別の件数を保持する軽量な状態値です。</summary>
internal readonly struct CatalogChartCollectionStateSnapshot
{
    /// <summary>共通集合版と件数を結び付けます。</summary>
    /// <param name="catalogRevision">共通集合版。</param>
    /// <param name="bmsRowCount">BMSの件数。</param>
    /// <param name="bmsonRowCount">BMSONの件数。</param>
    internal CatalogChartCollectionStateSnapshot(int catalogRevision, int bmsRowCount, int bmsonRowCount)
    {
        CatalogRevision = catalogRevision;

        BmsRowCount = bmsRowCount;
        BmsonRowCount = bmsonRowCount;
    }

    /// <summary>捕捉時点の共通集合版です。</summary>
    internal int CatalogRevision { get; }



    /// <summary>BMSの件数。</summary>
    internal int BmsRowCount { get; }

    /// <summary>BMSONの件数。</summary>
    internal int BmsonRowCount { get; }
}
