using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>所持譜面一件の、本体候補の照合キーと親フォルダです。比較キーは導入先推定と同じ正規化で作ります。</summary>
internal readonly record struct BaseCandidateKey(string Title, string Artist, string Folder);

/// <summary>索引へ反映する、所持譜面の所属数の増減です。</summary>
internal readonly record struct BaseCandidateDelta(BaseCandidateKey Key, int Count);

/// <summary>本体候補として推定した所持フォルダと、そこで一致した所持譜面数です。</summary>
/// <param name="Folder">一致した所持譜面の直接の親フォルダ。保存されたパスの表記のままです。</param>
/// <param name="ChartCount">そのフォルダで一致した所持譜面の数です。</param>
internal sealed record BaseCandidate(string Folder, int ChartCount);

/// <summary>
/// 所持譜面の原題・作者から、その曲の本体を持つフォルダを引く不変の派生索引です。形は「題名キー →（作者キー →（親フォルダ → 所属数））」です。
/// DBへ保存せず、<see cref="CatalogOwnedCollectionOwner"/> が所持譜面の現在値から作ります。
/// 不変なので、読取りは排他なしに一つの参照だけを読めば整合します。更新は変更に関わる経路だけを新しく作ります。
/// フォルダ名は大文字小文字を区別して保持し、差分適用と全構築の内容を常に一致させます。
/// </summary>
internal sealed class CatalogBaseCandidateIndex
{
    /// <summary>候補として返す最大件数です。</summary>
    internal const int MaxCandidates = 5;

    /// <summary>作者キー同士の類似度が、この値以上なら作者が近いとみなします。ちょうどこの値を含みます。</summary>
    internal const double ArtistSimilarityThreshold = 0.50;

    private readonly ImmutableDictionary<string, ImmutableDictionary<string, ImmutableDictionary<string, int>>> titles;

    private CatalogBaseCandidateIndex(ImmutableDictionary<string, ImmutableDictionary<string, ImmutableDictionary<string, int>>> titles)
    {
        this.titles = titles;
    }

    /// <summary>所持譜面を持たない索引です。</summary>
    internal static CatalogBaseCandidateIndex Empty { get; } = new(ImmutableDictionary.Create<string, ImmutableDictionary<string, ImmutableDictionary<string, int>>>(StringComparer.Ordinal));

    /// <summary>索引の入れ物の数です。空の入れ物が残っていないことの確認にも使います。</summary>
    internal (int Titles, int Artists, int Folders) Shape
    {
        get
        {
            int artists = 0, folders = 0;
            foreach (ImmutableDictionary<string, ImmutableDictionary<string, int>> artistMap in titles.Values)
            {
                artists += artistMap.Count;
                foreach (ImmutableDictionary<string, int> folderMap in artistMap.Values) { folders += folderMap.Count; }
            }
            return (titles.Count, artists, folders);
        }
    }

    /// <summary>全内容を（題名キー、作者キー、フォルダ、所属数）の整列した列として列挙します。内容の同値比較に使います。</summary>
    internal IReadOnlyList<(string Title, string Artist, string Folder, int Count)> Enumerate()
        => [.. titles.SelectMany(title => title.Value.SelectMany(artist => artist.Value
                .Select(folder => (title.Key, artist.Key, folder.Key, folder.Value))))
            .OrderBy(entry => entry.Item1, StringComparer.Ordinal)
            .ThenBy(entry => entry.Item2, StringComparer.Ordinal)
            .ThenBy(entry => entry.Item3, StringComparer.Ordinal)];

    /// <summary>
    /// 題名キーが完全一致し、作者キーが一致・項目側が空・または文字2-gramの類似度が0.50以上の所持フォルダを、
    /// 一致譜面数の降順・同数ならフォルダパスの大文字小文字を区別しない昇順で、最大5件返します。
    /// </summary>
    /// <param name="titleKey">項目の題名の比較キー。空なら候補なしです。</param>
    /// <param name="artistKey">項目の作者の比較キー。空なら譜面の作者によらず一致します。</param>
    internal IReadOnlyList<BaseCandidate> Find(string titleKey, string artistKey)
    {
        if (string.IsNullOrEmpty(titleKey) || !titles.TryGetValue(titleKey, out ImmutableDictionary<string, ImmutableDictionary<string, int>> artistMap))
        {
            return [];
        }
        artistKey ??= string.Empty;
        Dictionary<string, int> bigrams = null;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, ImmutableDictionary<string, int>> artist in artistMap)
        {
            bool match = artistKey.Length == 0 || string.Equals(artist.Key, artistKey, StringComparison.Ordinal);
            if (!match && artist.Key.Length > 0)
            {
                bigrams ??= InstallEstimationMetadataNormalizer.BuildBigramCounts(artistKey);
                match = InstallEstimationMetadataNormalizer.ComputeSimilarity(artistKey, artist.Key, bigrams) >= ArtistSimilarityThreshold;
            }
            if (!match) { continue; }
            foreach (KeyValuePair<string, int> folder in artist.Value)
            {
                counts[folder.Key] = counts.GetValueOrDefault(folder.Key) + folder.Value;
            }
        }
        return [.. counts
            .OrderByDescending(folder => folder.Value)
            .ThenBy(folder => folder.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(folder => folder.Key, StringComparer.Ordinal)
            .Take(MaxCandidates)
            .Select(folder => new BaseCandidate(folder.Key, folder.Value))];
    }

    /// <summary>所属数の増減を一括で反映した新しい索引を返します。0以下になった組は取り除き、空になった作者・題名の入れ物も残しません。</summary>
    internal CatalogBaseCandidateIndex Apply(IReadOnlyCollection<BaseCandidateDelta> deltas)
    {
        if (deltas == null || deltas.Count == 0) { return this; }
        var net = new Dictionary<BaseCandidateKey, int>();
        foreach (BaseCandidateDelta delta in deltas) { net[delta.Key] = net.GetValueOrDefault(delta.Key) + delta.Count; }
        ImmutableDictionary<string, ImmutableDictionary<string, ImmutableDictionary<string, int>>> next = titles;
        foreach (IGrouping<string, KeyValuePair<BaseCandidateKey, int>> titleGroup in net.Where(entry => entry.Value != 0).GroupBy(entry => entry.Key.Title, StringComparer.Ordinal))
        {
            ImmutableDictionary<string, ImmutableDictionary<string, int>> artistMap = next.GetValueOrDefault(titleGroup.Key)
                ?? ImmutableDictionary.Create<string, ImmutableDictionary<string, int>>(StringComparer.Ordinal);
            foreach (KeyValuePair<BaseCandidateKey, int> entry in titleGroup)
            {
                ImmutableDictionary<string, int> folderMap = artistMap.GetValueOrDefault(entry.Key.Artist)
                    ?? ImmutableDictionary.Create<string, int>(StringComparer.Ordinal);
                int count = folderMap.GetValueOrDefault(entry.Key.Folder) + entry.Value;
                folderMap = count > 0 ? folderMap.SetItem(entry.Key.Folder, count) : folderMap.Remove(entry.Key.Folder);
                artistMap = folderMap.IsEmpty ? artistMap.Remove(entry.Key.Artist) : artistMap.SetItem(entry.Key.Artist, folderMap);
            }
            next = artistMap.IsEmpty ? next.Remove(titleGroup.Key) : next.SetItem(titleGroup.Key, artistMap);
        }
        return ReferenceEquals(next, titles) ? this : new CatalogBaseCandidateIndex(next);
    }

    /// <summary>全構築のために所属数を集計し、不変の索引へ変換します。単一スレッドで使います。</summary>
    internal sealed class Builder
    {
        private readonly Dictionary<string, Dictionary<string, Dictionary<string, int>>> titles = new(StringComparer.Ordinal);

        /// <summary>譜面一件を所属数へ加えます。</summary>
        internal void Add(BaseCandidateKey key)
        {
            if (!titles.TryGetValue(key.Title, out Dictionary<string, Dictionary<string, int>> artists))
            {
                titles.Add(key.Title, artists = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal));
            }
            if (!artists.TryGetValue(key.Artist, out Dictionary<string, int> folders))
            {
                artists.Add(key.Artist, folders = new Dictionary<string, int>(StringComparer.Ordinal));
            }
            folders[key.Folder] = folders.GetValueOrDefault(key.Folder) + 1;
        }

        /// <summary>集計した内容を不変の索引にします。</summary>
        internal CatalogBaseCandidateIndex ToIndex()
        {
            ImmutableDictionary<string, ImmutableDictionary<string, ImmutableDictionary<string, int>>>.Builder root =
                ImmutableDictionary.CreateBuilder<string, ImmutableDictionary<string, ImmutableDictionary<string, int>>>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Dictionary<string, Dictionary<string, int>>> title in titles)
            {
                ImmutableDictionary<string, ImmutableDictionary<string, int>>.Builder artists =
                    ImmutableDictionary.CreateBuilder<string, ImmutableDictionary<string, int>>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, Dictionary<string, int>> artist in title.Value)
                {
                    artists.Add(artist.Key, ImmutableDictionary.CreateRange(StringComparer.Ordinal, artist.Value));
                }
                root.Add(title.Key, artists.ToImmutable());
            }
            return new CatalogBaseCandidateIndex(root.ToImmutable());
        }
    }
}
