using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>現在の選択範囲だけの entry・lookup・token 依存です。列値の別正本を保持しません。</summary>
internal sealed class PlaylistDetailDependencies
{
    private readonly Action<string> observeWork;
    private readonly Dictionary<BMSTableEntry, int> positions = [];
    private readonly HashSet<BMSTableEntry> entryChartInfoDependencies = [];
    private readonly Dictionary<OwnedChartToken, HashSet<BMSTableEntry>> tokens = [];
    private readonly Dictionary<PlaylistEntryLookupKey, HashSet<BMSTableEntry>> lookups = new(CatalogReadSnapshot.LookupKeyComparer);
    private readonly Dictionary<string, HashSet<BMSTableEntry>> md5s = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<BMSTableEntry>> sha256s = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>初回構築した所属列と依存を一度接続します。非表示行も含めます。</summary>
    internal PlaylistDetailDependencies(List<PlaylistDetailSourceRow> rows, Action<string> observeWork = null)
    {
        this.observeWork = observeWork;
        Rows = rows;
        for (int index = 0; index < rows.Count; index++)
        {
            observeWork?.Invoke("playlist_dependency_index_entry");
            Index(rows[index], index);
        }
    }

    internal List<PlaylistDetailSourceRow> Rows { get; }
    /// <summary>この元集合だけの参照bucketと実適用済み位置です。</summary>
    internal ChartListReferenceInput ReferenceInput;
    /// <summary>構築中・採用待ちに受信した、境界を保つ参照確定です。適用後に除去します。</summary>
    internal readonly List<PlaylistReferenceCommitReceipt> ReferenceCommits = [];
    /// <summary>未配送の捕捉位置を待つ一つの元receiptと投影です。索引や履歴の正本にはしません。</summary>
    internal readonly Dictionary<object, PlaylistPendingReferenceProjection> PendingReferenceProjections = [];
    /// <summary>新依存の挿入完了まで先取りしないscore範囲です。</summary>
    internal readonly List<ScoreSnapshotChange> PendingScores = [];
    /// <summary>新依存の挿入後に評価する詳細のMD5対象です。</summary>
    internal readonly HashSet<string> PendingDetailMd5s = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>新依存の挿入後に評価する詳細のSHA-256対象です。</summary>
    internal readonly HashSet<string> PendingDetailSha256s = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>詳細投影の短命保留を他の元receiptから識別します。</summary>
    internal readonly object DetailProjectionKey = new();

    /// <summary>捕捉済み参照入力に属するhash依存だけを調べます。</summary>
    internal bool IncludesReferenceKey(PlaylistEntryLookupKey key)
        => key.HasValue && (key.Kind == PlaylistEntryLookupKeyKind.Md5 ? md5s.ContainsKey(key.Hash) : sha256s.ContainsKey(key.Hash));

    /// <summary>変更した参照keyに依存する非表示行も含めて集めます。</summary>
    internal void AddReferenceDependents(PlaylistEntryLookupKey key, HashSet<BMSTableEntry> target)
        => AddDetailDependents(key.Kind == PlaylistEntryLookupKeyKind.Md5 ? [key.Hash] : [],
            key.Kind == PlaylistEntryLookupKeyKind.Sha256 ? [key.Hash] : [], target);
    internal bool TryGet(BMSTableEntry entry, out PlaylistDetailSourceRow row)
    {
        row = positions.TryGetValue(entry, out int index) ? Rows[index] : null;
        return row != null;
    }

    /// <summary>未所持詳細に依存する行だけを捕捉し、全 source の検査を避けます。</summary>
    internal IEnumerable<PlaylistDetailSourceRow> EntryChartInfoRows()
    {
        foreach (BMSTableEntry entry in new List<BMSTableEntry>(entryChartInfoDependencies))
        {
            if (TryGet(entry, out PlaylistDetailSourceRow row)) { yield return row; }
        }
    }

    internal void AddTokenDependents(OwnedChartToken token, HashSet<BMSTableEntry> target)
    {
        if (token != null && tokens.TryGetValue(token, out HashSet<BMSTableEntry> entries)) { target.UnionWith(entries); }
    }

    internal void AddLookupDependents(PlaylistEntryLookupKey key, HashSet<BMSTableEntry> target)
    {
        if (key.HasValue && lookups.TryGetValue(key, out HashSet<BMSTableEntry> entries)) { target.UnionWith(entries); }
    }

    internal void AddDetailDependents(IEnumerable<string> md5Keys, IEnumerable<string> sha256Keys, HashSet<BMSTableEntry> target)
    {
        foreach (string key in md5Keys ?? []) { if (key != null && md5s.TryGetValue(key, out HashSet<BMSTableEntry> entries)) { target.UnionWith(entries); } }
        foreach (string key in sha256Keys ?? []) { if (key != null && sha256s.TryGetValue(key, out HashSet<BMSTableEntry> entries)) { target.UnionWith(entries); } }
    }

    /// <summary>変更対象の投影とその逆引きだけを置換します。</summary>
    internal void Replace(PlaylistDetailSourceRow row)
    {
        if (!positions.TryGetValue(row.Entry, out int index)) { Add(row); return; }
        PlaylistDetailSourceRow previous = Rows[index];
        RemoveDependencies(previous);
        Rows[index] = row;
        Index(row, index);
        PruneReferenceInput(previous);
    }

    internal void Add(PlaylistDetailSourceRow row) { Rows.Add(row); Index(row, Rows.Count - 1); }
    /// <summary>対象を除去し、格納末尾の一件だけを移して位置辞書を更新します。表示順はEntryOrderが担当します。</summary>
    internal void Remove(BMSTableEntry entry)
    {
        if (!positions.Remove(entry, out int index)) { return; }
        PlaylistDetailSourceRow previous = Rows[index];
        RemoveDependencies(previous);
        int last = Rows.Count - 1;
        if (index != last)
        {
            // source の格納位置は表示順ではありません。確定 EntryOrder でのみ表示順を決めます。
            Rows[index] = Rows[last];
            positions[Rows[index].Entry] = index;
            observeWork?.Invoke("playlist_dependency_position_write");
        }
        Rows.RemoveAt(last);
        PruneReferenceInput(previous);
    }

    private void PruneReferenceInput(PlaylistDetailSourceRow previous)
    {
        PlaylistEntryLookupKey[] keys = [new(PlaylistEntryLookupKeyKind.Md5, previous.hash), new(PlaylistEntryLookupKeyKind.Md5, previous.EntryValue.Md5),
            new(PlaylistEntryLookupKeyKind.Sha256, previous.sha256), new(PlaylistEntryLookupKeyKind.Sha256, previous.EntryValue.Sha256)];
        ReferenceInput = ReferenceInput?.RemoveDependencies(keys.Where(key => key.HasValue && !IncludesReferenceKey(key)));
    }

    private void Index(PlaylistDetailSourceRow row, int index)
    {
        observeWork?.Invoke("playlist_dependency_index_write");
        positions[row.Entry] = index;
        if (row.HasEntryChartInfoDependency) { entryChartInfoDependencies.Add(row.Entry); }
        if (row.OwnedCurrent?.Token != null) { Add(tokens, row.OwnedCurrent.Token, row.Entry); }
        if (row.LookupKey.HasValue) { Add(lookups, row.LookupKey, row.Entry); }
        foreach (string value in new[] { row.hash, row.EntryValue.Md5 }) { if (!string.IsNullOrWhiteSpace(value)) { Add(md5s, value, row.Entry); } }
        foreach (string value in new[] { row.sha256, row.EntryValue.Sha256 }) { if (!string.IsNullOrWhiteSpace(value)) { Add(sha256s, value, row.Entry); } }
    }

    private void RemoveDependencies(PlaylistDetailSourceRow row)
    {
        entryChartInfoDependencies.Remove(row.Entry);
        if (row.OwnedCurrent?.Token != null) { Remove(tokens, row.OwnedCurrent.Token, row.Entry); }
        if (row.LookupKey.HasValue) { Remove(lookups, row.LookupKey, row.Entry); }
        foreach (string value in new[] { row.hash, row.EntryValue.Md5 }) { if (!string.IsNullOrWhiteSpace(value)) { Remove(md5s, value, row.Entry); } }
        foreach (string value in new[] { row.sha256, row.EntryValue.Sha256 }) { if (!string.IsNullOrWhiteSpace(value)) { Remove(sha256s, value, row.Entry); } }
    }

    private static void Add<TKey>(Dictionary<TKey, HashSet<BMSTableEntry>> index, TKey key, BMSTableEntry entry)
    {
        if (!index.TryGetValue(key, out HashSet<BMSTableEntry> entries)) { index.Add(key, entries = []); }
        entries.Add(entry);
    }

    private static void Remove<TKey>(Dictionary<TKey, HashSet<BMSTableEntry>> index, TKey key, BMSTableEntry entry)
    {
        if (index.TryGetValue(key, out HashSet<BMSTableEntry> entries) && entries.Remove(entry) && entries.Count == 0) { index.Remove(key); }
    }
}
