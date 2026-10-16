using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>捕捉したカタログ列を表示入力へ変換します。所属は保持せず、参照された投影だけを再利用します。</summary>
internal sealed class NormalLibrarySourceRows : IReadOnlyList<ChartListSourceRow>
{
    private readonly Func<ChartFile, ChartListSourceRow> project;
    private readonly ChartListSourceRow[] projections;
    private readonly ProjectionCache projectionCache;

    /// <summary>所属・基本値・版を一組として保持し、可変の正本を行取得時に検索しません。</summary>
    internal NormalLibrarySourceRows(BMSLibrary library, CatalogChartCollectionSnapshot snapshot,
        bool includeBmsonRows, Func<ChartFile, ChartListSourceRow> project, NormalLibrarySourceRows previous = null)
    {
        Library = library;
        Snapshot = snapshot;
        IncludeBmsonRows = includeBmsonRows;
        this.project = project;
        projections = new ChartListSourceRow[Count];
        // この型は通常一覧専用の同じ投影設定で作る。cacheは不変値の変換だけを共有し、所属を決めない。
        projectionCache = previous != null && ReferenceEquals(Library, previous.Library)
            ? previous.projectionCache : new ProjectionCache(snapshot.MembershipOrderIdentity, Count);
    }

    /// <summary>捕捉元の識別です。別ライブラリの投影採用を防ぎます。</summary>
    internal BMSLibrary Library { get; }
    /// <summary>この表示入力の所属・基本値・版です。</summary>
    internal CatalogChartCollectionSnapshot Snapshot { get; }
    /// <summary>捕捉列の形式範囲です。</summary>
    internal bool IncludeBmsonRows { get; }
    /// <summary>捕捉版が現在も採用可能かを返します。行値の解決には使用しません。</summary>
    internal bool IsCurrent => Library == null || Snapshot.OwnedCollectionVersion == Library.OwnedCollectionVersion;
    public int Count => Snapshot.BmsRows.Count + Snapshot.BmsonRows.Count;
    public ChartListSourceRow this[int index]
    {
        get
        {
            ChartListSourceRow row = Volatile.Read(ref projections[index]);
            return row ?? GetOrCreate(index, index < Snapshot.BmsRows.Count
                ? Snapshot.BmsRows[index] : Snapshot.BmsonRows[index - Snapshot.BmsRows.Count]);
        }
    }

    /// <summary>所属と基準順序の変更事実により、既存添字の再利用を判定します。</summary>
    internal bool HasSameMembershipOrder(NormalLibrarySourceRows other) => other != null
        && ReferenceEquals(Library, other.Library) && IncludeBmsonRows == other.IncludeBmsonRows
        && ReferenceEquals(Snapshot.MembershipOrderIdentity, other.Snapshot.MembershipOrderIdentity);

    public IEnumerator<ChartListSourceRow> GetEnumerator()
    {
        int index = 0;
        foreach (ChartFile chart in Snapshot.BmsRows)
        {
            yield return GetOrCreate(index++, chart);
        }
        foreach (ChartFile chart in Snapshot.BmsonRows)
        {
            yield return GetOrCreate(index++, chart);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>全件整列が必要になった時だけ、捕捉列を順次訪問して投影を準備します。既存の基本値に対応する投影は共有します。</summary>
    internal void PrepareForOrder()
    {
        foreach (ChartListSourceRow row in this) { }
    }

    /// <summary>現在の入力をcacheへ採用するときだけ、所属・順序変更に伴う退役投影を捕捉済み索引で回収します。</summary>
    internal void PruneProjectionCache()
    {
        lock (projectionCache.Rows)
        {
            if (ReferenceEquals(projectionCache.MembershipOrderIdentity, Snapshot.MembershipOrderIdentity))
            {
                return;
            }
            List<OwnedChartToken> retired = null;
            foreach (KeyValuePair<OwnedChartToken, ChartListSourceRow> pair in projectionCache.Rows)
            {
                if (!Snapshot.ContainsChart(pair.Value.CapturedChart))
                {
                    (retired ??= []).Add(pair.Key);
                }
            }
            foreach (OwnedChartToken token in retired ?? [])
            {
                projectionCache.Rows.Remove(token);
            }
            projectionCache.MembershipOrderIdentity = Snapshot.MembershipOrderIdentity;
        }
    }

    private ChartListSourceRow GetOrCreate(int index, ChartFile chart)
    {
        ChartListSourceRow row = Volatile.Read(ref projections[index]);
        if (row != null)
        {
            return row;
        }
        ChartListSourceRow next;
        lock (projectionCache.Rows)
        {
            if (chart.Token != null && projectionCache.Rows.TryGetValue(chart.Token, out ChartListSourceRow cached)
                && ReferenceEquals(cached.CapturedChart, chart))
            {
                next = cached;
            }
            else
            {
                next = project(chart);
                // 剪定後に旧入力の背景構築が続いても、退役項目を共有cacheへ戻さない。
                // 投影自体はこの入力の配列へ保持し、捕捉済みの値を読む契約を維持する。
                if (chart.Token != null
                    && ReferenceEquals(projectionCache.MembershipOrderIdentity, Snapshot.MembershipOrderIdentity))
                {
                    projectionCache.Rows[chart.Token] = next;
                }
            }
        }
        return Interlocked.CompareExchange(ref projections[index], next, null) ?? next;
    }

    /// <summary>投影だけを共有します。markerは剪定済み範囲を表し、cacheのキーや件数を一覧所属に使いません。</summary>
    private sealed class ProjectionCache(object membershipOrderIdentity, int capacity)
    {
        internal Dictionary<OwnedChartToken, ChartListSourceRow> Rows { get; } = new(capacity);
        internal object MembershipOrderIdentity { get; set; } = membershipOrderIdentity;
    }
}
