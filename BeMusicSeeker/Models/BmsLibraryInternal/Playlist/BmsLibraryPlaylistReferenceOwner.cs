using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>同じ表reader内で捕捉した参照headerです。表参照は識別だけに使い、比較時にlive属性を読みません。</summary>
internal sealed record PlaylistReferenceTableHeader(BMSTable Table, string Symbol, string Name, int? BmtSort, int TableOrder);

/// <summary>初回・表集合変更・実置換で必要な表の参照入力です。通常entry変更は既存receiptを使います。</summary>
internal sealed class PlaylistReferenceTableSnapshot
{
    /// <summary>呼出元が保持する同じ表reader内のheaderと有効entry keyだけを捕捉します。</summary>
    internal PlaylistReferenceTableSnapshot(BMSTable table, string symbol, string name, IEnumerable<BMSTableEntry> entries, int tableOrder = 0, Action<string> observe = null)
    {
        Header = new(table ?? throw new ArgumentNullException(nameof(table)), symbol ?? string.Empty, name ?? string.Empty, table.bmt_sort, tableOrder);
        var values = new List<PlaylistReferenceEntrySnapshot>();
        foreach (BMSTableEntry entry in entries ?? [])
        {
            observe?.Invoke("reference_entry_captured");
            if (entry?.is_removed == false) { values.Add(new(entry)); }
        }
        Entries = values.AsReadOnly();
    }
    /// <summary>値の読取りには使わない正本表の識別です。</summary>
    internal BMSTable Table => Header.Table;
    /// <summary>同じ表reader内で捕捉した表示headerです。</summary>
    internal PlaylistReferenceTableHeader Header { get; }
    /// <summary>実全置換で索引へ渡す有効entryの優先keyです。</summary>
    internal IReadOnlyList<PlaylistReferenceEntrySnapshot> Entries { get; }
}
/// <summary>既に順序が確定した表の表示文字列です。live属性を参照しません。</summary>
internal sealed record PlaylistReferenceTableDisplaySnapshot(string Symbol, string Name);
/// <summary>参照索引登録に必要なentry優先key一個です。entry値の第二正本は持ちません。</summary>
internal sealed class PlaylistReferenceEntrySnapshot
{
    /// <summary>同じ表reader内の有効entryからMD5優先のkeyを捕捉します。</summary>
    internal PlaylistReferenceEntrySnapshot(BMSTableEntry entry) { LookupKey = PlaylistEntryLookupKey.FromEntry(entry); }
    /// <summary>MD5がなければSHAだけを持つ登録keyです。</summary>
    internal PlaylistEntryLookupKey LookupKey { get; }
}

/// <summary>参照索引・確定版・不変差分・既存キュー登録を一つの排他内で確定します。配送起動は排他解放後です。</summary>
internal sealed class BmsLibraryPlaylistReferenceOwner
{
    private readonly object syncRoot = new();
    private readonly PlaylistReferenceIndex index = PlaylistReferenceIndex.Empty;
    private readonly NormalLibraryRefreshPublisher publisher;
    private readonly Action wake;
    private long indexRevision;
    /// <summary>既存キューへの確定登録と、排他解放後の任意配送起動を接続します。</summary>
    internal BmsLibraryPlaylistReferenceOwner(NormalLibraryRefreshPublisher publisher = null, Action wake = null)
    { this.publisher = publisher; this.wake = wake; }
    /// <summary>実参照格納・lookup・bucket・表示生成地点だけの任意observerです。集計状態は呼出側が所有します。</summary>
    internal Action<string> WorkObserver { get; set; }
    /// <summary>索引変更と不変通知登録を終えた最後の参照版を、索引の排他を待たず取得します。同じ値の捕捉にはCaptureを使います。</summary>
    internal long ReferenceRevision => Volatile.Read(ref indexRevision);
    /// <summary>MD5 bucketの存在を優先し、不在時だけSHAを引きます。</summary>
    internal PlaylistReferenceDisplay Find(string md5, string sha256)
    { lock (syncRoot) { WorkObserver?.Invoke("reference_lookup"); return index.Find(md5, sha256); } }
    /// <summary>捕捉済み所持譜面のhashから参照表示を引きます。</summary>
    internal PlaylistReferenceDisplay Find(ChartFile chart) => chart == null ? PlaylistReferenceDisplay.Empty : Find(chart.Md5, chart.Sha256);
    /// <summary>捕捉済み譜面参照のhashから参照表示を引きます。</summary>
    internal PlaylistReferenceDisplay Find(LibraryChartRef chart) => chart == null ? PlaylistReferenceDisplay.Empty : Find(chart.Md5, chart.Sha256);

    /// <summary>必要なMD5/SHA両キーを同じ版で捕捉します。存在する空bucketも結果に含め、全索引は複写しません。</summary>
    internal PlaylistReferenceReadSnapshot Capture(IEnumerable<PlaylistEntryLookupKey> keys, CancellationToken cancellationToken = default)
    {
        lock (syncRoot)
        {
            var values = new Dictionary<PlaylistEntryLookupKey, PlaylistReferenceDisplay>(CatalogReadSnapshot.LookupKeyComparer);
            foreach (PlaylistEntryLookupKey key in keys.Distinct(CatalogReadSnapshot.LookupKeyComparer))
            {
                cancellationToken.ThrowIfCancellationRequested(); WorkObserver?.Invoke("reference_lookup");
                if (key.HasValue && index.TryGet(key, out PlaylistReferenceDisplay value)) { values.Add(key, value); }
            }
            return new(indexRevision, values);
        }
    }
    /// <summary>同groupのentry差分を全guard解放後に一回適用します。headerだけの変更も同じ境界へ渡せます。</summary>
    internal PlaylistReferenceCommitReceipt Apply(IReadOnlyList<PlaylistEntryCommitReceipt> receipts, IEnumerable<PlaylistReferenceTableHeader> headers = null)
        => Commit(() => index.Apply(receipts, headers, WorkObserver));
    /// <summary>初回・実全置換で参照選択集合全体を採用します。</summary>
    internal PlaylistReferenceCommitReceipt SynchronizeReferenceBMSTables(IEnumerable<PlaylistReferenceTableSnapshot> snapshots)
        => Commit(() => index.ReplaceTables(snapshots, synchronize: true, WorkObserver));
    /// <summary>新規登録・表の実置換に必要な表だけを更新します。</summary>
    internal PlaylistReferenceCommitReceipt ReplaceTables(IEnumerable<PlaylistReferenceTableSnapshot> snapshots,
        IEnumerable<PlaylistReferenceTableHeader> headers = null, IEnumerable<BMSTable> removals = null)
        => Commit(() => index.ReplaceTables(snapshots, observe: WorkObserver, headers: headers, removals: removals));
    /// <summary>退役した表だけを除去します。</summary>
    internal PlaylistReferenceCommitReceipt RemoveTables(IEnumerable<BMSTable> tables)
        => Commit(() => index.RemoveTables(tables, WorkObserver));
    /// <summary>全体入力捕捉の前に既存索引の確定位置を取得します。</summary>
    internal long CaptureReferenceIndexRevision() { lock (syncRoot) { return indexRevision; } }
    /// <summary>全体捕捉と採用の間に確定した更新を上書きしないための短命な準備結果です。</summary>
    internal sealed record PlaylistReferenceSynchronizationPlan(long BaseRevision, IReadOnlyList<PlaylistReferenceTableSnapshot> Snapshots);
    /// <summary>全体捕捉を短命な準備結果へ固定します。別の完成索引は事前構築しません。</summary>
    internal PlaylistReferenceSynchronizationPlan PrepareReferenceBMSTableSynchronization(long baseRevision, IEnumerable<PlaylistReferenceTableSnapshot> snapshots)
        => new(baseRevision, Array.AsReadOnly(snapshots.ToArray()));
    /// <summary>捕捉後に独立参照更新が進んだ準備結果を採用しません。配送起動は排他解放後です。</summary>
    internal bool TryCommitReferenceBMSTableSynchronization(PlaylistReferenceSynchronizationPlan plan)
    {
        PlaylistReferenceCommitReceipt receipt;
        lock (syncRoot)
        {
            if (indexRevision != plan.BaseRevision) { return false; }
            receipt = Register(index.ReplaceTables(plan.Snapshots, synchronize: true, observe: WorkObserver));
        }
        if (receipt != null) { wake?.Invoke(); }
        return true;
    }
    private PlaylistReferenceCommitReceipt Commit(Func<PlaylistReferenceIndex.ChangeResult> apply)
    {
        PlaylistReferenceCommitReceipt receipt;
        lock (syncRoot)
        {
            PlaylistReferenceIndex.ChangeResult result = apply();
            receipt = Register(result);
        }
        if (receipt != null) { wake?.Invoke(); }
        return receipt;
    }
    private PlaylistReferenceCommitReceipt Register(PlaylistReferenceIndex.ChangeResult result)
    {
        if (!result.HasChanges) { return null; }
        long previousRevision = indexRevision;
        long nextRevision = previousRevision + 1;
        try
        {
            var receipt = new PlaylistReferenceCommitReceipt(previousRevision, nextRevision, result.Changes);
            publisher?.Register(receipt);
            return receipt;
        }
        finally
        {
            // 索引変更済みなら登録の失敗でも版を戻さず、元例外を保つ。配送起動より先に公開する。
            Volatile.Write(ref indexRevision, nextRevision);
        }
    }
}
