using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// playlist resolve indexへ登録するchart refとcanonical順を一組にした不変factです。
/// </summary>
internal sealed class PlaylistLibraryResolveChartFact
{
    private PlaylistLibraryResolveChartFact(
        LibraryChartKind kind,
        string path,
        string md5,
        string sha256,
        OwnedChartCanonicalOrderKey canonicalOrder,
        LibraryChartRef chart)
    {
        Kind = kind;
        Path = path;
        Md5 = md5;
        Sha256 = sha256;
        CanonicalOrder = canonicalOrder;
        Chart = chart;
    }

    /// <summary>chartのkindです。</summary>
    internal LibraryChartKind Kind { get; }

    /// <summary>加工前のexact pathです。</summary>
    internal string Path { get; }

    /// <summary>chartのMD5です。</summary>
    internal string Md5 { get; }

    /// <summary>chartのSHA-256です。</summary>
    internal string Sha256 { get; }

    /// <summary>canonical sequenceの安定順です。</summary>
    internal OwnedChartCanonicalOrderKey CanonicalOrder { get; }

    /// <summary>追加時に保持するimmutable chart refです。削除factではnullです。</summary>
    internal LibraryChartRef Chart { get; }

    /// <summary>
    /// 現在のchart refから、snapshotへ取り込める不変factを作成します。
    /// </summary>
    /// <param name="chart">現在のchart ref。</param>
    /// <param name="canonicalOrder">canonical sequenceの順序fact。</param>
    /// <returns>有効なchart fact。pathまたはMD5がない場合はnull。</returns>
    internal static PlaylistLibraryResolveChartFact FromChart(
        LibraryChartRef chart,
        OwnedChartCanonicalOrderKey canonicalOrder)
    {
        LibraryChartRef immutableChart = LibraryChartRef.FromImmutableSnapshot(chart);
        if (immutableChart == null
            || string.IsNullOrWhiteSpace(immutableChart.Path)
            || string.IsNullOrWhiteSpace(immutableChart.Md5))
        {
            return null;
        }

        return new PlaylistLibraryResolveChartFact(
            immutableChart.Kind,
            immutableChart.Path,
            immutableChart.Md5,
            immutableChart.Sha256,
            canonicalOrder,
            immutableChart);
    }

    /// <summary>
    /// 旧候補を除去するためのimmutable factを作成します。
    /// </summary>
    /// <param name="kind">chartのkind。</param>
    /// <param name="path">旧候補のexact path。</param>
    /// <param name="md5">旧候補のMD5。</param>
    /// <param name="sha256">旧候補のSHA-256。</param>
    /// <returns>削除fact。</returns>
    internal static PlaylistLibraryResolveChartFact ForRemoval(
        LibraryChartKind kind,
        string path,
        string md5,
        string sha256)
    {
        return string.IsNullOrWhiteSpace(path)
            ? null
            : new PlaylistLibraryResolveChartFact(
                kind,
                path,
                NormalizeHash(md5),
                NormalizeHash(sha256),
                OwnedChartCanonicalOrderKey.Missing,
                null);
    }

    private static string NormalizeHash(string hash)
    {
        return string.IsNullOrWhiteSpace(hash) ? null : hash.Trim();
    }
}

/// <summary>
/// playlist detail の entry hash から、現在の所持 chart 代表を解決する immutable snapshot です。
/// </summary>
internal sealed class PlaylistLibraryResolveIndexSnapshot
{
    private static readonly ImmutableDictionary<string, PlaylistLibraryResolveChartFact> EmptyMembership =
        ImmutableDictionary<string, PlaylistLibraryResolveChartFact>.Empty.WithComparers(StringComparer.Ordinal);

    private static readonly ImmutableDictionary<string, ImmutableArray<PlaylistLibraryResolveChartFact>> EmptyCandidateBuckets =
        ImmutableDictionary<string, ImmutableArray<PlaylistLibraryResolveChartFact>>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    private static readonly PlaylistLibraryResolveIndexSnapshot empty = new(
        EmptyMembership,
        EmptyCandidateBuckets,
        EmptyCandidateBuckets,
        version: 0,
        buildElapsedMs: 0L,
        invalidationVersion: 0,
        ownedCollectionVersion: 0,
        bmsRowsVersion: 0,
        bmsonRowsVersion: 0);

    private readonly ImmutableDictionary<string, PlaylistLibraryResolveChartFact> candidatesByIdentity;

    private readonly ImmutableDictionary<string, ImmutableArray<PlaylistLibraryResolveChartFact>> candidatesByMd5;

    private readonly ImmutableDictionary<string, ImmutableArray<PlaylistLibraryResolveChartFact>> candidatesBySha256;

    private PlaylistLibraryResolveIndexSnapshot(
        ImmutableDictionary<string, PlaylistLibraryResolveChartFact> candidatesByIdentity,
        ImmutableDictionary<string, ImmutableArray<PlaylistLibraryResolveChartFact>> candidatesByMd5,
        ImmutableDictionary<string, ImmutableArray<PlaylistLibraryResolveChartFact>> candidatesBySha256,
        int version,
        long buildElapsedMs,
        int invalidationVersion,
        int ownedCollectionVersion,
        int bmsRowsVersion,
        int bmsonRowsVersion)
    {
        this.candidatesByIdentity = candidatesByIdentity ?? EmptyMembership;
        this.candidatesByMd5 = candidatesByMd5 ?? EmptyCandidateBuckets;
        this.candidatesBySha256 = candidatesBySha256 ?? EmptyCandidateBuckets;
        Version = version;
        BuildElapsedMs = buildElapsedMs;
        InvalidationVersion = invalidationVersion;
        OwnedCollectionVersion = ownedCollectionVersion;
        BmsRowsVersion = bmsRowsVersion;
        BmsonRowsVersion = bmsonRowsVersion;
    }

    /// <summary>空のresolve indexです。</summary>
    internal static PlaylistLibraryResolveIndexSnapshot Empty => empty;

    /// <summary>BMSLibrary cache内のsnapshot版数です。</summary>
    internal int Version { get; }

    /// <summary>snapshot buildに要した時間です。</summary>
    internal long BuildElapsedMs { get; }

    /// <summary>build開始時点のinvalidation版数です。</summary>
    internal int InvalidationVersion { get; }

    /// <summary>build元のowned collection版数です。</summary>
    internal int OwnedCollectionVersion { get; }

    /// <summary>build元のBMS storage rows版数です。</summary>
    internal int BmsRowsVersion { get; }

    /// <summary>build元のbmson storage rows版数です。</summary>
    internal int BmsonRowsVersion { get; }

    /// <summary>現在のMD5 hash bucket数です。</summary>
    internal int Md5HashCount => candidatesByMd5.Count;

    /// <summary>現在のSHA-256 hash bucket数です。</summary>
    internal int Sha256HashCount => candidatesBySha256.Count;

    /// <summary>
    /// chart ref列挙からplaylist detail用resolve indexを構築します。
    /// 直接呼び出しでは入力順を同値canonical順として扱います。
    /// </summary>
    /// <param name="charts">登録対象のchart ref。</param>
    /// <param name="cancellationCheck">構築中に呼び出すcancellation callback。</param>
    /// <returns>playlist detail用resolve index。</returns>
    internal static PlaylistLibraryResolveIndexSnapshot FromLibraryChartRefs(
        IEnumerable<LibraryChartRef> charts,
        Action cancellationCheck = null)
    {
        long ordinal = 0L;
        var facts = new List<PlaylistLibraryResolveChartFact>();
        foreach (LibraryChartRef chart in charts ?? [])
        {
            cancellationCheck?.Invoke();
            if (chart == null)
            {
                continue;
            }

            facts.Add(PlaylistLibraryResolveChartFact.FromChart(
                chart,
                new OwnedChartCanonicalOrderKey(
                    chart.Kind == LibraryChartKind.Bmson ? ChartFileKind.Bmson : ChartFileKind.Bms,
                    null,
                    ordinal++,
                    usesCapturedPathOrder: false)));
        }
        return FromLibraryChartFacts(facts, cancellationCheck, null);
    }

    /// <summary>
    /// canonical順序factを伴うchart集合からresolve indexを構築します。
    /// </summary>
    /// <param name="facts">canonical順序を捕捉したchart facts。</param>
    /// <param name="cancellationCheck">構築中に呼び出すcancellation callback。</param>
    /// <param name="storeWorkObserver">実格納処理を観測する任意の内部observer。</param>
    /// <returns>playlist detail用resolve index。</returns>
    internal static PlaylistLibraryResolveIndexSnapshot FromLibraryChartFacts(
        IEnumerable<PlaylistLibraryResolveChartFact> facts,
        Action cancellationCheck = null,
        Action<string> storeWorkObserver = null)
    {
        var candidates = new Dictionary<string, PlaylistLibraryResolveChartFact>(StringComparer.Ordinal);
        storeWorkObserver?.Invoke("playlist_resolve_full_root_enumeration");
        foreach (PlaylistLibraryResolveChartFact fact in facts ?? [])
        {
            cancellationCheck?.Invoke();
            PlaylistLibraryResolveChartFact candidate = CreateCandidate(fact);
            if (candidate == null)
            {
                continue;
            }
            storeWorkObserver?.Invoke("playlist_resolve_full_root_key_visited");
            candidates[CreateIdentityKey(candidate.Kind, candidate.Path)] = candidate;
        }

        var md5Buckets = new Dictionary<string, List<PlaylistLibraryResolveChartFact>>(StringComparer.OrdinalIgnoreCase);
        var sha256Buckets = new Dictionary<string, List<PlaylistLibraryResolveChartFact>>(StringComparer.OrdinalIgnoreCase);
        foreach (PlaylistLibraryResolveChartFact candidate in candidates.Values)
        {
            AddMutableBucket(md5Buckets, candidate.Md5, candidate);
            AddMutableBucket(sha256Buckets, candidate.Sha256, candidate);
        }

        ImmutableDictionary<string, PlaylistLibraryResolveChartFact> membership =
            ImmutableDictionary.CreateRange(StringComparer.Ordinal, candidates);
        ImmutableDictionary<string, ImmutableArray<PlaylistLibraryResolveChartFact>> immutableMd5Buckets =
            CreateImmutableBuckets(md5Buckets);
        ImmutableDictionary<string, ImmutableArray<PlaylistLibraryResolveChartFact>> immutableSha256Buckets =
            CreateImmutableBuckets(sha256Buckets);
        storeWorkObserver?.Invoke("playlist_resolve_root_capture");
        return new PlaylistLibraryResolveIndexSnapshot(
            membership,
            immutableMd5Buckets,
            immutableSha256Buckets,
            version: 0,
            buildElapsedMs: 0L,
            invalidationVersion: 0,
            ownedCollectionVersion: 0,
            bmsRowsVersion: 0,
            bmsonRowsVersion: 0);
    }

    /// <summary>
    /// 旧候補の除去と新候補の追加を、影響するMD5/SHA bucketだけへ適用します。
    /// </summary>
    /// <param name="removals">旧exact pathの削除facts。</param>
    /// <param name="additions">現在のcanonical chart追加facts。</param>
    /// <param name="storeWorkObserver">実格納処理を観測する任意の内部observer。</param>
    /// <param name="nextSnapshot">適用後snapshot。</param>
    /// <returns>factsが現在snapshotと整合して適用できた場合はtrue。</returns>
    internal bool TryApplyDelta(
        IEnumerable<PlaylistLibraryResolveChartFact> removals,
        IEnumerable<PlaylistLibraryResolveChartFact> additions,
        Action<string> storeWorkObserver,
        out PlaylistLibraryResolveIndexSnapshot nextSnapshot)
    {
        List<PlaylistLibraryResolveChartFact> removalFacts = [.. (removals ?? [])
            .Where(fact => fact != null)];
        List<PlaylistLibraryResolveChartFact> additionFacts = [.. (additions ?? [])
            .Where(fact => fact != null)];
        if (removalFacts.Count == 0 && additionFacts.Count == 0)
        {
            nextSnapshot = this;
            return true;
        }

        ImmutableDictionary<string, PlaylistLibraryResolveChartFact> nextMembership = candidatesByIdentity;
        ImmutableDictionary<string, ImmutableArray<PlaylistLibraryResolveChartFact>> nextMd5Buckets = candidatesByMd5;
        ImmutableDictionary<string, ImmutableArray<PlaylistLibraryResolveChartFact>> nextSha256Buckets = candidatesBySha256;
        var affectedMd5 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var affectedSha256 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (PlaylistLibraryResolveChartFact fact in removalFacts)
        {
            string identityKey = CreateIdentityKey(fact.Kind, fact.Path);
            if (!nextMembership.TryGetValue(identityKey, out PlaylistLibraryResolveChartFact existing)
                || !MatchesRemovalFact(existing, fact))
            {
                nextSnapshot = this;
                return false;
            }

            nextMembership = nextMembership.Remove(identityKey);
            if (!TryRemoveCandidate(
                nextMd5Buckets,
                existing.Md5,
                identityKey,
                out nextMd5Buckets))
            {
                nextSnapshot = this;
                return false;
            }
            affectedMd5.Add(existing.Md5);
            if (!string.IsNullOrWhiteSpace(existing.Sha256))
            {
                if (!TryRemoveCandidate(
                    nextSha256Buckets,
                    existing.Sha256,
                    identityKey,
                    out nextSha256Buckets))
                {
                    nextSnapshot = this;
                    return false;
                }
                affectedSha256.Add(existing.Sha256);
            }
        }

        foreach (PlaylistLibraryResolveChartFact fact in additionFacts)
        {
            PlaylistLibraryResolveChartFact candidate = CreateCandidate(fact);
            if (candidate == null)
            {
                nextSnapshot = this;
                return false;
            }

            string candidateIdentityKey = CreateIdentityKey(candidate.Kind, candidate.Path);
            if (nextMembership.TryGetValue(candidateIdentityKey, out PlaylistLibraryResolveChartFact existing))
            {
                nextMembership = nextMembership.Remove(candidateIdentityKey);
                if (!TryRemoveCandidate(
                    nextMd5Buckets,
                    existing.Md5,
                    candidateIdentityKey,
                    out nextMd5Buckets))
                {
                    nextSnapshot = this;
                    return false;
                }
                affectedMd5.Add(existing.Md5);
                if (!string.IsNullOrWhiteSpace(existing.Sha256))
                {
                    if (!TryRemoveCandidate(
                        nextSha256Buckets,
                        existing.Sha256,
                        candidateIdentityKey,
                        out nextSha256Buckets))
                    {
                        nextSnapshot = this;
                        return false;
                    }
                    affectedSha256.Add(existing.Sha256);
                }
            }

            nextMembership = nextMembership.SetItem(candidateIdentityKey, candidate);
            nextMd5Buckets = AddCandidate(nextMd5Buckets, candidate.Md5, candidate);
            affectedMd5.Add(candidate.Md5);
            if (!string.IsNullOrWhiteSpace(candidate.Sha256))
            {
                nextSha256Buckets = AddCandidate(nextSha256Buckets, candidate.Sha256, candidate);
                affectedSha256.Add(candidate.Sha256);
            }
        }

        storeWorkObserver?.Invoke("playlist_resolve_delta_apply");
        foreach (string _ in affectedMd5)
        {
            storeWorkObserver?.Invoke("playlist_resolve_bucket_update");
        }
        foreach (string _ in affectedSha256)
        {
            storeWorkObserver?.Invoke("playlist_resolve_bucket_update");
        }
        storeWorkObserver?.Invoke("playlist_resolve_root_capture");
        nextSnapshot = new PlaylistLibraryResolveIndexSnapshot(
            nextMembership,
            nextMd5Buckets,
            nextSha256Buckets,
            Version,
            BuildElapsedMs,
            InvalidationVersion,
            OwnedCollectionVersion,
            BmsRowsVersion,
            BmsonRowsVersion);
        return true;
    }

    /// <summary>
    /// mutation後のsource版数を、同じimmutable rootの新しいsnapshotへ捕捉します。
    /// </summary>
    /// <param name="version">新しいsnapshot版数。</param>
    /// <param name="ownedCollectionVersion">owned collection版数。</param>
    /// <param name="bmsRowsVersion">BMS storage rows版数。</param>
    /// <param name="bmsonRowsVersion">bmson storage rows版数。</param>
    /// <param name="buildElapsedMs">初回build時間。</param>
    /// <param name="invalidationVersion">invalidation版数。</param>
    /// <returns>rootを共有するmetadata更新snapshot。</returns>
    internal PlaylistLibraryResolveIndexSnapshot WithMetadata(
        int version,
        long buildElapsedMs,
        int invalidationVersion,
        int ownedCollectionVersion,
        int bmsRowsVersion,
        int bmsonRowsVersion)
    {
        return new PlaylistLibraryResolveIndexSnapshot(
            candidatesByIdentity,
            candidatesByMd5,
            candidatesBySha256,
            version,
            buildElapsedMs,
            invalidationVersion,
            ownedCollectionVersion,
            bmsRowsVersion,
            bmsonRowsVersion);
    }

    /// <summary>
    /// 指定したMD5の全候補をcanonical順で返します。
    /// </summary>
    /// <param name="md5">検索するMD5。</param>
    /// <returns>候補chart ref列挙。</returns>
    internal IReadOnlyList<LibraryChartRef> GetMd5Candidates(string md5)
    {
        return GetCandidates(candidatesByMd5, md5);
    }

    /// <summary>
    /// 指定したkindとexact pathの候補が存在するか返します。
    /// </summary>
    /// <param name="kind">候補kind。</param>
    /// <param name="path">候補exact path。</param>
    /// <returns>候補が存在すればtrue。</returns>
    internal bool ContainsCandidate(LibraryChartKind kind, string path)
    {
        return candidatesByIdentity.ContainsKey(CreateIdentityKey(kind, path));
    }

    /// <summary>
    /// playlist entryのmd5 / sha256から、現在ライブラリに存在するchartを解決します。
    /// </summary>
    /// <param name="entry">解決対象のplaylist entry。</param>
    /// <returns>一致したchart。見つからない場合はnull。</returns>
    internal LibraryChartRef ResolveChartForPlaylistEntry(BMSTableEntry entry)
    {
        PlaylistEntryLookupKey lookupKey = PlaylistEntryLookupKey.FromEntry(entry);
        return ResolveChartForPlaylistLookupKey(lookupKey);
    }

    /// <summary>
    /// playlist entry snapshotのmd5 / sha256から、現在ライブラリに存在するchartを解決します。
    /// </summary>
    /// <param name="md5">playlist entryのMD5。</param>
    /// <param name="sha256">playlist entryのSHA256。</param>
    /// <returns>一致したchart。見つからない場合はnull。</returns>
    internal LibraryChartRef ResolveChartForPlaylistHash(string md5, string sha256)
    {
        PlaylistEntryLookupKey lookupKey = !string.IsNullOrWhiteSpace(md5)
            ? new PlaylistEntryLookupKey(PlaylistEntryLookupKeyKind.Md5, md5)
            : (!string.IsNullOrWhiteSpace(sha256)
                ? new PlaylistEntryLookupKey(PlaylistEntryLookupKeyKind.Sha256, sha256)
                : default);
        return ResolveChartForPlaylistLookupKey(lookupKey);
    }

    private LibraryChartRef ResolveChartForPlaylistLookupKey(PlaylistEntryLookupKey lookupKey)
    {
        if (!lookupKey.HasValue)
        {
            return null;
        }
        if (lookupKey.Kind == PlaylistEntryLookupKeyKind.Md5
            && candidatesByMd5.TryGetValue(lookupKey.Hash, out ImmutableArray<PlaylistLibraryResolveChartFact> md5Candidates)
            && !md5Candidates.IsDefaultOrEmpty)
        {
            return md5Candidates[0].Chart;
        }
        if (lookupKey.Kind == PlaylistEntryLookupKeyKind.Sha256
            && candidatesBySha256.TryGetValue(lookupKey.Hash, out ImmutableArray<PlaylistLibraryResolveChartFact> sha256Candidates)
            && !sha256Candidates.IsDefaultOrEmpty)
        {
            return sha256Candidates[0].Chart;
        }
        return null;
    }

    private static PlaylistLibraryResolveChartFact CreateCandidate(PlaylistLibraryResolveChartFact fact)
    {
        if (fact == null
            || fact.Chart == null
            || string.IsNullOrWhiteSpace(fact.Path)
            || string.IsNullOrWhiteSpace(fact.Md5))
        {
            return null;
        }
        return fact;
    }

    private static bool MatchesRemovalFact(
        PlaylistLibraryResolveChartFact candidate,
        PlaylistLibraryResolveChartFact removal)
    {
        return candidate != null
            && removal != null
            && string.Equals(candidate.Md5, removal.Md5, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(removal.Sha256)
                || string.Equals(candidate.Sha256, removal.Sha256, StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateIdentityKey(LibraryChartKind kind, string path)
    {
        return (kind == LibraryChartKind.Bmson ? "bmson" : "bms")
            + "\u001f"
            + (path ?? string.Empty);
    }

    private static void AddMutableBucket(
        Dictionary<string, List<PlaylistLibraryResolveChartFact>> buckets,
        string hash,
        PlaylistLibraryResolveChartFact candidate)
    {
        if (string.IsNullOrWhiteSpace(hash) || candidate == null)
        {
            return;
        }
        if (!buckets.TryGetValue(hash, out List<PlaylistLibraryResolveChartFact> candidates))
        {
            candidates = [];
            buckets[hash] = candidates;
        }
        candidates.Add(candidate);
    }

    private static ImmutableDictionary<string, ImmutableArray<PlaylistLibraryResolveChartFact>> CreateImmutableBuckets(
        Dictionary<string, List<PlaylistLibraryResolveChartFact>> buckets)
    {
        var builder = EmptyCandidateBuckets.ToBuilder();
        foreach (KeyValuePair<string, List<PlaylistLibraryResolveChartFact>> pair in buckets ?? [])
        {
            pair.Value.Sort(CompareCandidates);
            builder[pair.Key] = ImmutableArray.CreateRange(pair.Value);
        }
        return builder.ToImmutable();
    }

    private static ImmutableDictionary<string, ImmutableArray<PlaylistLibraryResolveChartFact>> AddCandidate(
        ImmutableDictionary<string, ImmutableArray<PlaylistLibraryResolveChartFact>> buckets,
        string hash,
        PlaylistLibraryResolveChartFact candidate)
    {
        if (string.IsNullOrWhiteSpace(hash) || candidate == null)
        {
            return buckets;
        }
        ImmutableArray<PlaylistLibraryResolveChartFact> existing = buckets.TryGetValue(hash, out ImmutableArray<PlaylistLibraryResolveChartFact> current)
            ? current
            : ImmutableArray<PlaylistLibraryResolveChartFact>.Empty;
        ImmutableArray<PlaylistLibraryResolveChartFact>.Builder builder = ImmutableArray.CreateBuilder<PlaylistLibraryResolveChartFact>(existing.Length + 1);
        bool inserted = false;
        foreach (PlaylistLibraryResolveChartFact item in existing)
        {
            if (!inserted && CompareCandidates(candidate, item) < 0)
            {
                builder.Add(candidate);
                inserted = true;
            }
            builder.Add(item);
        }
        if (!inserted)
        {
            builder.Add(candidate);
        }
        return buckets.SetItem(hash, builder.ToImmutable());
    }

    private static bool TryRemoveCandidate(
        ImmutableDictionary<string, ImmutableArray<PlaylistLibraryResolveChartFact>> buckets,
        string hash,
        string identityKey,
        out ImmutableDictionary<string, ImmutableArray<PlaylistLibraryResolveChartFact>> nextBuckets)
    {
        nextBuckets = buckets;
        if (string.IsNullOrWhiteSpace(hash)
            || !buckets.TryGetValue(hash, out ImmutableArray<PlaylistLibraryResolveChartFact> existing))
        {
            return false;
        }
        int removeIndex = -1;
        for (int index = 0; index < existing.Length; index++)
        {
            if (string.Equals(
                    CreateIdentityKey(existing[index].Kind, existing[index].Path),
                    identityKey,
                    StringComparison.Ordinal))
            {
                removeIndex = index;
                break;
            }
        }
        if (removeIndex < 0)
        {
            return false;
        }
        if (existing.Length == 1)
        {
            nextBuckets = buckets.Remove(hash);
            return true;
        }
        ImmutableArray<PlaylistLibraryResolveChartFact>.Builder builder = ImmutableArray.CreateBuilder<PlaylistLibraryResolveChartFact>(existing.Length - 1);
        for (int index = 0; index < existing.Length; index++)
        {
            if (index != removeIndex)
            {
                builder.Add(existing[index]);
            }
        }
        nextBuckets = buckets.SetItem(hash, builder.ToImmutable());
        return true;
    }

    private static int CompareCandidates(
        PlaylistLibraryResolveChartFact left,
        PlaylistLibraryResolveChartFact right)
    {
        int pathCompare = StringComparer.OrdinalIgnoreCase.Compare(left?.Path, right?.Path);
        if (pathCompare != 0)
        {
            return pathCompare;
        }
        int orderCompare = (left?.CanonicalOrder ?? OwnedChartCanonicalOrderKey.Missing)
            .CompareTo(right?.CanonicalOrder ?? OwnedChartCanonicalOrderKey.Missing);
        if (orderCompare != 0)
        {
            return orderCompare;
        }
        return StringComparer.Ordinal.Compare(
            left == null ? null : CreateIdentityKey(left.Kind, left.Path),
            right == null ? null : CreateIdentityKey(right.Kind, right.Path));
    }

    private static IReadOnlyList<LibraryChartRef> GetCandidates(
        ImmutableDictionary<string, ImmutableArray<PlaylistLibraryResolveChartFact>> buckets,
        string hash)
    {
        if (string.IsNullOrWhiteSpace(hash)
            || !buckets.TryGetValue(hash, out ImmutableArray<PlaylistLibraryResolveChartFact> candidates)
            || candidates.IsDefaultOrEmpty)
        {
            return [];
        }
        var result = new List<LibraryChartRef>(candidates.Length);
        foreach (PlaylistLibraryResolveChartFact candidate in candidates)
        {
            result.Add(candidate.Chart);
        }
        return result;
    }
}
