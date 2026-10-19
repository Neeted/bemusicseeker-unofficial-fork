using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>共通現在値の集合、既存の排他境界、集合版と派生索引を所有します。保存行の鏡像を保持しません。</summary>
internal sealed partial class CatalogOwnedCollectionOwner
{
    private readonly object gate = new();
    private OwnedChartCollectionState collection;
    private readonly ICatalogStorageSequenceWorkObserver sequenceWorkObserver;

    /// <summary>構築時に有効な空の共通集合を作り、既存の排他と処理量の診断を引き継ぎます。</summary>
    internal CatalogOwnedCollectionOwner(ICatalogStorageSequenceWorkObserver sequenceWorkObserver = null)
    {
        this.sequenceWorkObserver = sequenceWorkObserver;
        collection = OwnedChartCollectionState.FromCharts([], CancellationToken.None, sequenceWorkObserver);
    }
    private int collectionVersion;
    private readonly object hashIndexSnapshotGate = new();
    private OwnedChartHashIndexRoot hashIndexRoot;
    private OwnedChartHashIndexVersionedSnapshot hashIndexSnapshot;
    private int hashIndexSnapshotVersion;
    private int hashIndexInvalidationVersion;
    private int digestMutationWindowDepth;

    /// <summary>確定済み値の短い読書き境界です。</summary>
    internal ReaderWriterLockSlimWrapper WriteGate { get; } = new();
    /// <summary>共通現在値と派生参照索引を同じ時点で更新・捕捉する集合排他です。</summary>
    internal object Gate => gate;
    /// <summary>排他境界の内側で使用する共通現在値と索引の正本です。</summary>
    internal OwnedChartCollectionState Collection => collection;
    /// <summary>所持現在値のすべての実変更を同時確定したカタログ版です。</summary>
    internal int CatalogRevision => Volatile.Read(ref collectionVersion);


    internal Action<string> StoreWorkObserver { get; set; }
    /// <summary>同じ永続的な順序列から BMS の現在値だけを捕捉する読取り専用ビューです。</summary>
    internal IReadOnlyList<ChartFile> BmsRows { get { lock (gate) { return collection.CreateCollectionView().BmsCharts; } } }
    /// <summary>同じ永続的な順序列から BMSON の現在値だけを捕捉する読取り専用ビューです。</summary>
    internal IReadOnlyList<ChartFile> BmsonRows { get { lock (gate) { return collection.CreateCollectionView().BmsonCharts; } } }
    /// <summary>現在の集合版だけを捕捉します。独立した保存行版は持ちません。</summary>
    internal OwnedChartCollectionVersionSnapshot CaptureVersionSnapshot() => new(CatalogRevision);
    /// <summary>形式別の共通現在値と集合版を、全件複製せず同じ排他境界で捕捉します。</summary>
    internal CatalogChartCollectionSnapshot CaptureSnapshot(bool includeBmsonRows = true)
    {
        lock (gate)
        {
            OwnedChartCollectionView view = collection.CreateCollectionView(includeBmsonRows);
            lock (lockPlaylistLibraryResolveIndexSnapshot)
            {
                return new(view.BmsCharts, view.BmsonCharts, CatalogRevision, collection.MembershipOrderIdentity, view,
                    playlistLibraryResolveIndexSnapshot);
            }
        }
    }
    /// <summary>形式別の件数と集合版を全件列挙せず捕捉します。</summary>
    internal CatalogChartCollectionStateSnapshot CaptureStateSnapshot()
    {
        lock (gate) { OwnedChartCollectionView view = collection.CreateCollectionView(); return new(CatalogRevision, view.BmsCharts.Count, view.BmsonCharts.Count); }
    }
    /// <summary>既存のハッシュ変更範囲を開始します。</summary>
    internal void BeginDigestMutationWindow() => Interlocked.Increment(ref digestMutationWindowDepth);
    /// <summary>既存のハッシュ変更範囲を終了します。</summary>
    internal void EndDigestMutationWindow() => Interlocked.Decrement(ref digestMutationWindowDepth);
    /// <summary>既存のハッシュ変更範囲が公開前に進行しているかを返します。</summary>
    internal bool IsDigestMutationWindowActive() => Volatile.Read(ref digestMutationWindowDepth) > 0;
    /// <summary>取消を監視しながら既存のハッシュ変更範囲の終了を待ちます。</summary>
    internal void WaitForDigestMutationWindowIdle(CancellationToken token = default)
    {
        while (IsDigestMutationWindowActive()) { token.ThrowIfCancellationRequested(); Thread.Sleep(20); }
    }

    /// <summary>
    /// owned hash rootを破棄し、次回consumer利用時にfull buildへ戻します。
    /// </summary>
    internal void InvalidateHashIndexSnapshot()
    {
        lock (hashIndexSnapshotGate)
        {
            hashIndexRoot = null;
            hashIndexSnapshot = null;
            hashIndexInvalidationVersion++;
        }
    }

    /// <summary>
    /// 所持 hash の optional root が構築済みかを返します。
    /// </summary>
    /// <returns>構築済みなら true。</returns>
    internal bool IsHashIndexWarm()
    {
        lock (hashIndexSnapshotGate)
        {
            return hashIndexRoot != null;
        }
    }

    /// <summary>構築済みの所持hash存在集合の内容版を、現在の確定区間から取得します。索引構築と集合複写は行いません。</summary>
    /// <param name="version">warmなら既存のhash内容版。coldなら0。</param>
    /// <returns>既存hash rootがwarmならtrue。全置換・失効後のcoldはfalse。</returns>
    internal bool TryGetOwnedHashContentVersion(out int version)
    {
        using (WriteGate.GetReaderGuard())
        {
            lock (gate)
                lock (hashIndexSnapshotGate)
                {
                    version = hashIndexRoot == null ? 0 : hashIndexSnapshotVersion;
                    return hashIndexRoot != null;
                }
        }
    }

    /// <summary>現在値から必要時だけハッシュ索引を構築します。通常の局所変更では既存の索引へ差分を適用します。</summary>
    internal OwnedChartHashIndexVersionedSnapshot GetHashIndexSnapshot(CancellationToken cancellationToken, out bool cacheHit, out int staleRetryCount)
    {
        staleRetryCount = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WaitForDigestMutationWindowIdle(cancellationToken);
            using (WriteGate.GetReaderGuard())
            {
                lock (gate)
                    lock (hashIndexSnapshotGate)
                    {
                        if (IsHashIndexSnapshotCurrent(hashIndexSnapshot, CatalogRevision)) { cacheHit = true; return hashIndexSnapshot; }
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        cacheHit = hashIndexRoot != null;
                        if (hashIndexRoot == null)
                        {
                            hashIndexRoot = OwnedChartHashIndexRoot.Create(collection.CreateOwnedHashIndexSnapshot(cancellationToken, StoreWorkObserver));
                            hashIndexSnapshotVersion = Math.Max(1, hashIndexSnapshotVersion + 1);
                            StoreWorkObserver?.Invoke("owned_hash_root_capture");
                        }
                        hashIndexSnapshot = CreateHashIndexVersionedSnapshotUnsafe(watch.ElapsedMilliseconds, CatalogRevision);
                        return hashIndexSnapshot;
                    }
            }
        }
    }
    private bool IsHashIndexSnapshotCurrent(OwnedChartHashIndexVersionedSnapshot snapshot, int version)
        => snapshot != null && snapshot.CatalogRevision == version && snapshot.InvalidationVersion == hashIndexInvalidationVersion;
    private OwnedChartHashIndexVersionedSnapshot CreateHashIndexVersionedSnapshotUnsafe(long elapsed, int version)
        => OwnedChartHashIndexVersionedSnapshot.CreateFromRoot(hashIndexRoot, hashIndexSnapshotVersion, elapsed, hashIndexInvalidationVersion, version, StoreWorkObserver);

    /// <summary>DB一回読込みまたは確定走査の共通値で集合を置換します。既存の識別は入力が明示的に継承した場合だけ保持します。</summary>
    internal void ReplaceCharts(IEnumerable<ChartFile> bmsCharts, IEnumerable<ChartFile> bmsonCharts, bool replaceBms = true, bool replaceBmson = true)
    {
        Commit(state =>
        {
            if (replaceBms)
            {
                state.ReplaceKindCharts(ChartFileKind.Bms, bmsCharts);
            }

            if (replaceBmson)
            {
                state.ReplaceKindCharts(ChartFileKind.Bmson, bmsonCharts);
            }
        }, isFullReplacement: true);
    }
    /// <summary>共通現在値を一回置換し、同じ集合から形式別の読取り値を捕捉します。</summary>
    internal CatalogChartCollectionSnapshot ReplaceChartsAndCaptureSnapshot(IEnumerable<ChartFile> bmsCharts, IEnumerable<ChartFile> bmsonCharts)
    {
        using (WriteGate.GetWriterGuard())
        {
            lock (gate)
            {
                ReplaceCharts(bmsCharts, bmsonCharts);
                return CaptureSnapshot();
            }
        }
    }
    /// <summary>走査が排他内で構築した共通集合を正本として受け取り、任意のハッシュ索引を失効させます。</summary>
    internal bool ApplyBuiltCollection(OwnedChartCollectionState value)
    {
        if (value == null)
        {
            return false;
        }

        OwnedChartCollectionView view = value.CreateCollectionView();
        ReplaceCharts(view.BmsCharts, view.BmsonCharts);
        return true;
    }
    /// <summary>DBで確定した項目削除と内部移転を、共通現在値と隣接索引へ一回適用します。</summary>
    /// <returns>同じ確定時の現在値・版・旧新差分。後続entryへはこのAfterを共有します。</returns>
    internal CatalogCommitReceipt ApplyMutation(IReadOnlyList<OwnedChartRemoveRequest> removals, IReadOnlyList<LibraryChartPathChange> paths, out bool normalized)
    {
        normalized = false;
        return Commit(state =>
        {
            if (removals?.Count > 0)
            {
                state.RemoveChartRequests(removals);
            }

            if (paths?.Count > 0)
            {
                state.ApplyPathChanges(paths);
            }
        });
    }
    /// <summary>DB 確定前に追加・差替え対象の共通現在値を検査します。</summary>
    internal void ValidateChartUpsert(IEnumerable<ChartFile> values) { lock (gate) { collection.ValidateCharts(values); } }
    /// <summary>DB 確定した追加・差替えを現在値と索引へ一回適用し、新しい所持識別を発行します。</summary>
    internal bool ApplyCommittedChartUpsert(IReadOnlyList<ChartFile> values, out bool normalized)
    {
        bool orderNormalized = false;
        CatalogCommitReceipt receipt = Commit(state => orderNormalized = state.UpsertCharts(values));
        normalized = orderNormalized;
        if (orderNormalized)
        {
            InvalidatePlaylistLibraryResolveIndexSnapshot();
        }

        return receipt.HasChanges;
    }
    /// <summary>同じ所持識別の内容変更を共通現在値と温まったハッシュ索引へ反映します。</summary>
    internal bool ApplyDigestChanges(IReadOnlyList<LibraryChartDigestChange> changes)
    {
        return Commit(state => state.ApplyDigestChanges(changes)).HasChanges;
    }
    /// <summary>共通現在値を維持し、必要時に再構築するハッシュ索引だけを無効化します。</summary>
    internal void Invalidate() { lock (gate) { ClearHashIndexUnsafe(); } }
    /// <summary>走査で継承されない所持識別の削除対象を、差分の確定前に捕捉します。</summary>
    internal bool TryCaptureFileScanRemovedCharts(IReadOnlyList<string> deletedPaths,
        IReadOnlyList<string> deletedBmsonPaths, IReadOnlyList<ChartFile> nextFiles, IReadOnlyList<ChartFile> nextBmson,
        out List<ChartFile> removedCharts)
    {
        lock (gate)
        {
            var retained = new HashSet<OwnedChartToken>((nextFiles ?? []).Concat(nextBmson ?? []).Where(chart => chart?.Token != null).Select(chart => chart.Token));
            OwnedChartCollectionView view = collection.CreateCollectionView();
            removedCharts = [.. view.BmsCharts.Concat(view.BmsonCharts)
                .Where(chart => !retained.Contains(chart.Token))];
            return true;
        }
    }
    private void ClearHashIndexUnsafe()
    {
        lock (hashIndexSnapshotGate)
        {
            if (hashIndexRoot == null && hashIndexSnapshot == null)
            {
                return;
            }

            hashIndexRoot = null; hashIndexSnapshot = null; hashIndexInvalidationVersion++;
            StoreWorkObserver?.Invoke("owned_hash_full_invalidate");
        }
    }
}
