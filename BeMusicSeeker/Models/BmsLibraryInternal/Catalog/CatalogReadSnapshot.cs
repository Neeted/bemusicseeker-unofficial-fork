using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>必要項目だけの現在値と、同じ確定時点のカタログ版です。長寿命の候補索引へ表示値を保存しません。</summary>
internal sealed class CatalogReadSnapshot
{
    /// <summary>hashの大文字小文字を区別しない共通lookup key比較です。</summary>
    internal static IEqualityComparer<PlaylistEntryLookupKey> LookupKeyComparer { get; } = new KeyComparer();
    private sealed class KeyComparer : IEqualityComparer<PlaylistEntryLookupKey>
    {
        public bool Equals(PlaylistEntryLookupKey left, PlaylistEntryLookupKey right)
            => left.Kind == right.Kind && StringComparer.OrdinalIgnoreCase.Equals(left.Hash, right.Hash);
        public int GetHashCode(PlaylistEntryLookupKey key)
            => HashCode.Combine(key.Kind, key.Hash == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(key.Hash));
    }

    /// <summary>owner の読取り区間内で捕捉した値を固定します。</summary>
    internal CatalogReadSnapshot(int revision, IDictionary<OwnedChartToken, ChartFile> charts,
        IDictionary<PlaylistEntryLookupKey, LibraryChartRef> resolved = null)
    {
        CatalogRevision = revision;
        Charts = new ReadOnlyDictionary<OwnedChartToken, ChartFile>(charts);
        ResolvedCandidates = new ReadOnlyDictionary<PlaylistEntryLookupKey, LibraryChartRef>(resolved ?? new Dictionary<PlaylistEntryLookupKey, LibraryChartRef>(LookupKeyComparer));
    }
    /// <summary>全ての値と同時捕捉した版です。</summary>
    internal int CatalogRevision { get; }
    /// <summary>捕捉時に生存したtokenだけの不変現在値です。</summary>
    internal IReadOnlyDictionary<OwnedChartToken, ChartFile> Charts { get; }
    /// <summary>同じ読取り区間で選択した候補です。MD5優先と候補順を保ちます。</summary>
    internal IReadOnlyDictionary<PlaylistEntryLookupKey, LibraryChartRef> ResolvedCandidates { get; }
    /// <summary>捕捉した同tokenの値を返し、退役tokenを別項目へ救済しません。</summary>
    internal ChartFile Resolve(LibraryChartRef reference)
        => reference?.Token != null && Charts.TryGetValue(reference.Token, out ChartFile chart) ? chart : null;
    /// <summary>選択候補と現在値を一緒に返します。</summary>
    internal ChartFile Resolve(PlaylistEntryLookupKey key)
        => ResolvedCandidates.TryGetValue(key, out LibraryChartRef reference) ? Resolve(reference) : null;
}

internal sealed partial class CatalogOwnedCollectionOwner
{
    /// <summary>必要な参照だけを同じ版で捕捉します。入力の退役tokenは除外します。</summary>
    internal CatalogReadSnapshot CaptureReadSnapshot(IEnumerable<LibraryChartRef> references)
    {
        using (WriteGate.GetReaderGuard())
        {
            lock (gate)
            {
                var charts = new Dictionary<OwnedChartToken, ChartFile>();
                foreach (LibraryChartRef reference in references ?? [])
                {
                    ChartFile current = collection.ResolveCurrentChart(reference);
                    StoreWorkObserver?.Invoke("catalog_current_reference_lookup");
                    if (current?.Token != null)
                    {
                        charts[current.Token] = current;
                    }
                }
                return new(CatalogRevision, charts);
            }
        }
    }
    /// <summary>候補解決と必要tokenの現在値を同じ読取り区間で捕捉します。</summary>
    internal CatalogReadSnapshot CapturePlaylistReadSnapshot(IEnumerable<PlaylistEntryLookupKey> keys, CancellationToken cancellationToken = default)
    {
        using (WriteGate.GetReaderGuard())
        {
            lock (gate)
            {
                PlaylistLibraryResolveIndexSnapshot index = GetPlaylistLibraryResolveIndexSnapshot(cancellationToken, out _, out _);
                var resolved = new Dictionary<PlaylistEntryLookupKey, LibraryChartRef>(CatalogReadSnapshot.LookupKeyComparer);
                var charts = new Dictionary<OwnedChartToken, ChartFile>();
                foreach (PlaylistEntryLookupKey key in keys ?? [])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    LibraryChartRef reference = index.ResolveChartForPlaylistLookupKey(key);
                    resolved[key] = reference;
                    ChartFile current = collection.ResolveCurrentChart(reference);
                    StoreWorkObserver?.Invoke("catalog_current_reference_lookup");
                    if (current?.Token != null)
                    {
                        charts[current.Token] = current;
                    }
                }
                return new(CatalogRevision, charts, resolved);
            }
        }
    }
}
